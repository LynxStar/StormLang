using System;
using System.Collections.Generic;
using System.Linq;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.Frontend;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.IR;

/// <summary>
/// Lowers a validated <see cref="StormGrammar"/> (effective grammar) into a
/// <see cref="GrammarIR"/>. The lowering performs:
/// <list type="bullet">
///   <item>Fragment inlining — replaces fragment references in token expressions
///         with the fragment's body expression.</item>
///   <item>Synthetic token creation — materializes anonymous string literals
///         appearing in rule expressions as synthetic <see cref="TokenIR"/> entries.</item>
///   <item>Declaration-order index assignment for tokens.</item>
///   <item>Reference resolution — all references become IR-level pointers
///         (<see cref="TokenRefIR"/> / <see cref="RuleRefIR"/>).</item>
/// </list>
/// </summary>
public static class GrammarIRLowering
{
    /// <summary>
    /// Result of the lowering pass.
    /// </summary>
    public sealed class LoweringResult
    {
        public GrammarIR? Grammar { get; }
        public IReadOnlyList<StormDiagnostic> Diagnostics { get; }
        public bool Succeeded => Grammar != null && !Diagnostics.Any(d => d.Severity == StormDiagnosticSeverity.Error);

        public LoweringResult(GrammarIR? grammar, IReadOnlyList<StormDiagnostic> diagnostics)
        {
            Grammar = grammar;
            Diagnostics = diagnostics;
        }
    }

    /// <summary>
    /// Lowers the given effective grammar to a <see cref="GrammarIR"/>.
    /// </summary>
    public static LoweringResult Lower(StormGrammar grammar)
    {
        if(grammar == null)
        {
            throw new ArgumentNullException(nameof(grammar));
        }

        var diagnostics = new List<StormDiagnostic>();

        // ---------------------------------------------------------------
        // 0. Scan for StormModeDecl nodes to build mode name list and
        //    a mapping from declaration name → enclosing mode name.
        // ---------------------------------------------------------------
        var modeNames = new List<string>(grammar.ModeNames);
        var declNameToMode = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach(var decl in grammar.Declarations)
        {
            if(decl is StormModeDecl modeDecl)
            {
                foreach(var innerDecl in modeDecl.Declarations)
                {
                    declNameToMode[innerDecl.Name] = modeDecl.Name;
                }
            }
        }

        // ---------------------------------------------------------------
        // 1. Collect fragments for inlining (mode-scoped)
        // ---------------------------------------------------------------
        var fragmentsByName = new Dictionary<(string Mode, string Name), StormDeclaration>();
        foreach(var decl in grammar.Declarations)
        {
            if(decl is StormFragmentDecl)
            {
                var mode = declNameToMode.TryGetValue(decl.Name, out var m) ? m : "default";
                fragmentsByName[(mode, decl.Name)] = decl;
            }
        }

        // Also check namespaced declarations for fragments
        foreach(var kvp in grammar.NamespacedDeclarations)
        {
            if(kvp.Value is StormFragmentDecl)
            {
                fragmentsByName[("default", kvp.Key)] = kvp.Value;
            }
        }

        // ---------------------------------------------------------------
        // 2. Build TokenIR entries (explicit tokens with fragments inlined)
        // ---------------------------------------------------------------
        var tokenIRs = new List<TokenIR>();
        var tokensByName = new Dictionary<string, TokenIR>(StringComparer.Ordinal);
        var tokenIndex = 0;

        foreach(var decl in grammar.Declarations)
        {
            if(decl is StormTokenDecl or StormHiddenTokenDecl)
            {
                var isHidden = decl is StormHiddenTokenDecl;
                var mode = declNameToMode.TryGetValue(decl.Name, out var m) ? m : "default";
                var modeAction = ConvertModeAction(decl);
                var inlinedExpr = InlineFragments(decl.Body, fragmentsByName, mode, new HashSet<string>(StringComparer.Ordinal), diagnostics);
                var tokenIR = new TokenIR(decl.Name, inlinedExpr, isHidden, tokenIndex, mode: mode, modeAction: modeAction);
                tokenIRs.Add(tokenIR);
                tokensByName[decl.Name] = tokenIR;
                tokenIndex++;
            }
        }

        // Also process namespaced token declarations
        foreach(var kvp in grammar.NamespacedDeclarations)
        {
            if(kvp.Value is StormTokenDecl or StormHiddenTokenDecl)
            {
                if(!tokensByName.ContainsKey(kvp.Key))
                {
                    var isHidden = kvp.Value is StormHiddenTokenDecl;
                    var modeAction = ConvertModeAction(kvp.Value);
                    var inlinedExpr = InlineFragments(kvp.Value.Body, fragmentsByName, "default", new HashSet<string>(StringComparer.Ordinal), diagnostics);
                    var tokenIR = new TokenIR(kvp.Key, inlinedExpr, isHidden, tokenIndex, mode: "default", modeAction: modeAction);
                    tokenIRs.Add(tokenIR);
                    tokensByName[kvp.Key] = tokenIR;
                    tokenIndex++;
                }
            }
        }

        // ---------------------------------------------------------------
        // 3. Collect rule declarations (pre-pass to build RuleIR shells
        //    so we can resolve rule references)
        // ---------------------------------------------------------------
        var ruleShells = new Dictionary<string, RuleIR>(StringComparer.Ordinal);
        var ruleOrder = new List<(string Name, StormDeclaration Decl)>();

        foreach(var decl in grammar.Declarations)
        {
            if(decl is StormRuleDecl ruleDecl)
            {
                // Create a shell with empty alternatives — we'll fill them in step 5
                var isMemo = ruleDecl.IsMemo;
                var shell = new RuleIR(decl.Name, new List<RuleAlternativeIR>(), isMemo);
                ruleShells[decl.Name] = shell;
                ruleOrder.Add((decl.Name, decl));
            }
        }

        foreach(var kvp in grammar.NamespacedDeclarations)
        {
            if(kvp.Value is StormRuleDecl nsRuleDecl && !ruleShells.ContainsKey(kvp.Key))
            {
                var isMemo = nsRuleDecl.IsMemo;
                var shell = new RuleIR(kvp.Key, new List<RuleAlternativeIR>(), isMemo);
                ruleShells[kvp.Key] = shell;
                ruleOrder.Add((kvp.Key, kvp.Value));
            }
        }

        // ---------------------------------------------------------------
        // 4. First pass over rule expressions: collect anonymous string
        //    literals and create synthetic tokens for them
        // ---------------------------------------------------------------
        var syntheticTokens = new Dictionary<string, TokenIR>(StringComparer.Ordinal);

        foreach(var (name, decl) in ruleOrder)
        {
            CollectAnonymousStringLiterals(decl.Body, syntheticTokens, tokensByName, ref tokenIndex);
        }

        // Append synthetic tokens to the token list
        foreach(var synth in syntheticTokens.Values)
        {
            tokenIRs.Add(synth);
            tokensByName[synth.Name] = synth;
        }

        // ---------------------------------------------------------------
        // 5. Lower rule expressions with all references resolved.
        //    IMPORTANT: We populate the existing shell objects in-place
        //    (by adding to their mutable List<RuleAlternativeIR>) so that
        //    RuleRefIR nodes created during lowering — which capture a
        //    reference to the shell — see the final alternatives.
        //    Creating new RuleIR objects would leave stale references
        //    with empty alternative lists, breaking FIRST-set computation.
        // ---------------------------------------------------------------
        var ruleIRs = new List<RuleIR>();

        // Build a name→declaration lookup for inline block lowering
        var ruleDeclarations = new Dictionary<string, StormDeclaration>(StringComparer.Ordinal);
        foreach(var (name, decl) in ruleOrder)
        {
            ruleDeclarations[name] = decl;
        }

        foreach(var (name, decl) in ruleOrder)
        {
            var alternatives = LowerRuleAlternatives(decl.Body, tokensByName, ruleShells, syntheticTokens, ruleDeclarations, diagnostics);
            foreach(var shape in alternatives.SelectMany(alternative => UnemittableShapes.Find(name, alternative)))
            {
                diagnostics.Add(new StormDiagnostic(
                    StormDiagnosticIds.STORM_UNSUPPORTED_CONSTRUCT,
                    StormDiagnosticSeverity.Error,
                    shape,
                    null, decl.Line, decl.Column));
            }

            var shell = ruleShells[name];
            // The shell was created with a mutable List<RuleAlternativeIR>;
            // cast back and populate it so all existing RuleRefIR pointers
            // see the real alternatives.
            var mutableAlts = (List<RuleAlternativeIR>)shell.Alternatives;
            mutableAlts.AddRange(alternatives);
            ruleIRs.Add(shell);
        }

        if(diagnostics.Any(d => d.Severity == StormDiagnosticSeverity.Error))
        {
            return new LoweringResult(null, diagnostics);
        }

        var grammarIR = new GrammarIR(tokenIRs, ruleIRs, modeNames);
        return new LoweringResult(grammarIR, diagnostics);
    }

    // ---------------------------------------------------------------
    //  Fragment inlining
    // ---------------------------------------------------------------

    /// <summary>
    /// Recursively inlines fragment references in a token expression.
    /// Fragments are resolved first within the current mode, then falling
    /// back to the default mode.
    /// </summary>
    private static ExpressionIR InlineFragments(
        StormExpression expr,
        Dictionary<(string Mode, string Name), StormDeclaration> fragmentsByName,
        string currentMode,
        HashSet<string> inliningStack,
        List<StormDiagnostic> diagnostics)
    {
        switch(expr)
        {
            case StormReferenceExpr refExpr:
                if(TryResolveFragment(fragmentsByName, currentMode, refExpr.Name, out var fragment))
                {
                    if(!inliningStack.Add(refExpr.Name))
                    {
                        diagnostics.Add(new StormDiagnostic(
                            StormDiagnosticIds.STORM_UNSUPPORTED_CONSTRUCT,
                            StormDiagnosticSeverity.Error,
                            $"Circular fragment reference detected: '{refExpr.Name}'",
                            null, 0, 0));
                        return new StringLiteralIR(""); // placeholder
                    }

                    var inlined = InlineFragments(fragment.Body, fragmentsByName, currentMode, inliningStack, diagnostics);
                    _ = inliningStack.Remove(refExpr.Name);
                    return new InlineFragmentIR(refExpr.Name, inlined);
                }
                // Not a fragment — leave as-is for now; this is a token-to-token reference
                // which will be resolved later. For token expressions, non-fragment references
                // are references to other tokens.
                return new StringLiteralIR(refExpr.Name); // placeholder — shouldn't happen in valid grammars

            case StormQualifiedReferenceExpr qualRef:
                var qualName = qualRef.Qualifier + "." + qualRef.Name;
                if(TryResolveFragment(fragmentsByName, currentMode, qualName, out var qualFragment))
                {
                    if(!inliningStack.Add(qualName))
                    {
                        diagnostics.Add(new StormDiagnostic(
                            StormDiagnosticIds.STORM_UNSUPPORTED_CONSTRUCT,
                            StormDiagnosticSeverity.Error,
                            $"Circular fragment reference detected: '{qualName}'",
                            null, 0, 0));
                        return new StringLiteralIR("");
                    }

                    var inlined = InlineFragments(qualFragment.Body, fragmentsByName, currentMode, inliningStack, diagnostics);
                    _ = inliningStack.Remove(qualName);
                    return new InlineFragmentIR(qualName, inlined);
                }

                return new StringLiteralIR(qualName); // placeholder

            case StormSequenceExpr seqExpr:
                var seqElements = new List<ExpressionIR>();
                foreach(var element in seqExpr.Elements)
                {
                    seqElements.Add(InlineFragments(element, fragmentsByName, currentMode, inliningStack, diagnostics));
                }

                return new SequenceIR(seqElements);

            case StormAlternationExpr altExpr:
                var altElements = new List<ExpressionIR>();
                foreach(var alt in altExpr.Alternatives)
                {
                    altElements.Add(InlineFragments(alt, fragmentsByName, currentMode, inliningStack, diagnostics));
                }

                return new AlternationIR(altElements);

            case StormOptionalExpr optExpr:
                return new OptionalIR(InlineFragments(optExpr.Inner, fragmentsByName, currentMode, inliningStack, diagnostics));

            case StormZeroOrMoreExpr zomExpr:
                return new ZeroOrMoreIR(InlineFragments(zomExpr.Inner, fragmentsByName, currentMode, inliningStack, diagnostics));

            case StormOneOrMoreExpr oomExpr:
                return new OneOrMoreIR(InlineFragments(oomExpr.Inner, fragmentsByName, currentMode, inliningStack, diagnostics));

            case StormGroupExpr groupExpr:
                return InlineFragments(groupExpr.Inner, fragmentsByName, currentMode, inliningStack, diagnostics);

            case StormStringLiteralExpr strExpr:
                return new StringLiteralIR(strExpr.Value);

            case StormCharClassExpr ccExpr:
                return new CharClassIR(ccExpr.Text);

            case StormRegexLiteralExpr regexExpr:
                return new RegexLiteralIR(regexExpr.Pattern);

            case StormLabeledExpr labeledExpr:
                // Labels don't appear in token expressions normally, but handle gracefully
                return InlineFragments(labeledExpr.Element, fragmentsByName, currentMode, inliningStack, diagnostics);

            case StormNamedAlternative namedAlt:
                // Named alternatives don't appear in token expressions, but handle gracefully
                return InlineFragments(namedAlt.Expression, fragmentsByName, currentMode, inliningStack, diagnostics);

            default:
                diagnostics.Add(new StormDiagnostic(
                    StormDiagnosticIds.STORM_UNSUPPORTED_CONSTRUCT,
                    StormDiagnosticSeverity.Error,
                    $"Unsupported expression type in token expression: {expr.GetType().Name}",
                    null, 0, 0));
                return new StringLiteralIR("");
        }
    }

    /// <summary>
    /// Resolves a fragment by name, first looking in the current mode,
    /// then falling back to the default mode.
    /// </summary>
    private static bool TryResolveFragment(
        Dictionary<(string Mode, string Name), StormDeclaration> fragmentsByName,
        string currentMode,
        string fragmentName,
        out StormDeclaration fragment)
    {
        // Try current mode first
        if(fragmentsByName.TryGetValue((currentMode, fragmentName), out fragment!))
        {
            return true;
        }

        // Fall back to default mode if not already in default
        if(currentMode != "default" && fragmentsByName.TryGetValue(("default", fragmentName), out fragment!))
        {
            return true;
        }

        fragment = null!;
        return false;
    }

    /// <summary>
    /// Converts a <see cref="StormModeAction"/> from the AST to a <see cref="ModeActionIR"/>.
    /// </summary>
    private static ModeActionIR ConvertModeAction(StormDeclaration decl)
    {
        var action = decl switch
        {
            StormTokenDecl tokenDecl => tokenDecl.Action,
            StormHiddenTokenDecl hiddenDecl => hiddenDecl.Action,
            _ => null
        };

        return action switch
        {
            StormPushAction pushAction => new PushModeIR(pushAction.ModeName),
            StormPopAction => new PopModeIR(),
            _ => new NoModeActionIR()
        };
    }

    // ---------------------------------------------------------------
    //  Anonymous string literal collection
    // ---------------------------------------------------------------

    /// <summary>
    /// Walks a rule expression and collects all anonymous string literals,
    /// creating synthetic <see cref="TokenIR"/> entries for each unique literal
    /// that no declared token spells.
    /// </summary>
    private static void CollectAnonymousStringLiterals(
        StormExpression expr,
        Dictionary<string, TokenIR> syntheticTokens,
        Dictionary<string, TokenIR> existingTokens,
        ref int tokenIndex)
    {
        switch(expr)
        {
            case StormStringLiteralExpr strExpr:
                var synthName = SyntheticTokenName(strExpr.Value);
                if(DeclaredTokenSpelling(strExpr.Value, existingTokens.Values) == null && !syntheticTokens.ContainsKey(synthName) && !existingTokens.ContainsKey(synthName))
                {
                    var synthToken = new TokenIR(
                        synthName,
                        new StringLiteralIR(strExpr.Value),
                        isHidden: false,
                        declarationIndex: tokenIndex,
                        isSynthetic: true);
                    syntheticTokens[synthName] = synthToken;
                    tokenIndex++;
                }

                break;

            case StormSequenceExpr seqExpr:
                foreach(var element in seqExpr.Elements)
                {
                    CollectAnonymousStringLiterals(element, syntheticTokens, existingTokens, ref tokenIndex);
                }

                break;

            case StormAlternationExpr altExpr:
                foreach(var alt in altExpr.Alternatives)
                {
                    CollectAnonymousStringLiterals(alt, syntheticTokens, existingTokens, ref tokenIndex);
                }

                break;

            case StormOptionalExpr optExpr:
                CollectAnonymousStringLiterals(optExpr.Inner, syntheticTokens, existingTokens, ref tokenIndex);
                break;

            case StormZeroOrMoreExpr zomExpr:
                CollectAnonymousStringLiterals(zomExpr.Inner, syntheticTokens, existingTokens, ref tokenIndex);
                break;

            case StormOneOrMoreExpr oomExpr:
                CollectAnonymousStringLiterals(oomExpr.Inner, syntheticTokens, existingTokens, ref tokenIndex);
                break;

            case StormGroupExpr groupExpr:
                CollectAnonymousStringLiterals(groupExpr.Inner, syntheticTokens, existingTokens, ref tokenIndex);
                break;

            case StormPredicateExpr predicateExpr:
                CollectAnonymousStringLiterals(predicateExpr.Inner, syntheticTokens, existingTokens, ref tokenIndex);
                break;

            case StormLabeledExpr labeledExpr:
                CollectAnonymousStringLiterals(labeledExpr.Element, syntheticTokens, existingTokens, ref tokenIndex);
                break;

            case StormNamedAlternative namedAlt:
                CollectAnonymousStringLiterals(namedAlt.Expression, syntheticTokens, existingTokens, ref tokenIndex);
                break;

            case StormInlineBlockExpr inlineBlock:
                // Walk into the prefix rule's body and each arm's target rule body.
                // The prefix rule and arm target rules are walked independently when
                // their own declarations are processed. However, we still walk nested
                // inline blocks that appear as arm expressions.
                if(inlineBlock.Prefix is StormReferenceExpr)
                {
                    foreach(var arm in inlineBlock.Arms)
                    {
                        if(arm.Expression is StormInlineBlockExpr nestedBlock)
                        {
                            CollectAnonymousStringLiterals(nestedBlock, syntheticTokens, existingTokens, ref tokenIndex);
                        }
                    }
                }

                break;

            // References, char classes — no string literals to collect
            default:
                break;
        }
    }

    /// <summary>
    /// A synthetic token is lexed in the default mode after every declared token, so a declared default-mode token
    /// spelled exactly like the literal always wins the tie and the synthetic one never appears. The literal names that
    /// token instead, a hidden one included, just as writing its name would. A token of another mode never competes,
    /// and a pattern that merely admits the text is not recognised here.
    /// </summary>
    internal static TokenIR? DeclaredTokenSpelling(string literal, IEnumerable<TokenIR> tokens)
    {
        return tokens
            .Where(token => !token.IsSynthetic && token.Mode == "default" && Spells(token.Expression, literal))
            .OrderBy(token => token.DeclarationIndex)
            .FirstOrDefault();
    }

    private static bool Spells(ExpressionIR pattern, string literal)
    {
        return pattern switch
        {
            StringLiteralIR text => text.Value == literal,
            InlineFragmentIR fragment => Spells(fragment.Body, literal),
            _ => false,
        };
    }

    /// <summary>
    /// Generates a synthetic token name for an anonymous string literal.
    /// E.g., <c>"+"</c> → <c>__SYNTH_PLUS</c>, <c>"fn"</c> → <c>__SYNTH_fn</c>.
    /// </summary>
    internal static string SyntheticTokenName(string literalValue)
    {
        // Build a safe identifier from the literal value
        var safeName = new System.Text.StringBuilder("__SYNTH_");
        foreach(var c in literalValue)
        {
            if(char.IsLetterOrDigit(c) || c == '_')
            {
                _ = safeName.Append(c);
            }
            else
            {
                // Map common operators to readable names
                _ = c switch
                {
                    '+' => safeName.Append("PLUS"),
                    '-' => safeName.Append("MINUS"),
                    '*' => safeName.Append("STAR"),
                    '/' => safeName.Append("SLASH"),
                    '=' => safeName.Append("EQ"),
                    '<' => safeName.Append("LT"),
                    '>' => safeName.Append("GT"),
                    '!' => safeName.Append("BANG"),
                    '&' => safeName.Append("AMP"),
                    '|' => safeName.Append("PIPE"),
                    '(' => safeName.Append("LPAREN"),
                    ')' => safeName.Append("RPAREN"),
                    '{' => safeName.Append("LBRACE"),
                    '}' => safeName.Append("RBRACE"),
                    '[' => safeName.Append("LBRACKET"),
                    ']' => safeName.Append("RBRACKET"),
                    '.' => safeName.Append("DOT"),
                    ',' => safeName.Append("COMMA"),
                    ':' => safeName.Append("COLON"),
                    ';' => safeName.Append("SEMI"),
                    '?' => safeName.Append("QUESTION"),
                    '@' => safeName.Append("AT"),
                    '#' => safeName.Append("HASH"),
                    '%' => safeName.Append("PERCENT"),
                    '^' => safeName.Append("CARET"),
                    '~' => safeName.Append("TILDE"),
                    _ => safeName.Append($"U{(int)c:X4}"),
                };
            }
        }

        return safeName.ToString();
    }

    // ---------------------------------------------------------------
    //  Rule expression lowering
    // ---------------------------------------------------------------

    /// <summary>
    /// Lowers a rule's body expression into a list of <see cref="RuleAlternativeIR"/>.
    /// </summary>
    private static IReadOnlyList<RuleAlternativeIR> LowerRuleAlternatives(
        StormExpression body,
        Dictionary<string, TokenIR> tokensByName,
        Dictionary<string, RuleIR> rulesByName,
        Dictionary<string, TokenIR> syntheticTokens,
        Dictionary<string, StormDeclaration> ruleDeclarations,
        List<StormDiagnostic> diagnostics)
    {
        var alternatives = new List<RuleAlternativeIR>();

        // If the body is an alternation, each alternative becomes a RuleAlternativeIR.
        // Otherwise, the entire body is a single alternative.
        if(body is StormAlternationExpr altExpr)
        {
            foreach(var alt in altExpr.Alternatives)
            {
                alternatives.Add(LowerSingleAlternative(alt, tokensByName, rulesByName, syntheticTokens, ruleDeclarations, diagnostics));
            }
        }
        else
        {
            alternatives.Add(LowerSingleAlternative(body, tokensByName, rulesByName, syntheticTokens, ruleDeclarations, diagnostics));
        }

        return alternatives;
    }

    /// <summary>
    /// Lowers a single alternative expression, extracting any <c>as Name</c>
    /// label and field labels.
    /// </summary>
    private static RuleAlternativeIR LowerSingleAlternative(
        StormExpression expr,
        Dictionary<string, TokenIR> tokensByName,
        Dictionary<string, RuleIR> rulesByName,
        Dictionary<string, TokenIR> syntheticTokens,
        Dictionary<string, StormDeclaration> ruleDeclarations,
        List<StormDiagnostic> diagnostics)
    {
        string? altName = null;
        var bodyExpr = expr;

        // Check if this alternative has an `as Name` wrapper
        if(expr is StormNamedAlternative namedAlt)
        {
            altName = namedAlt.AlternativeName;
            bodyExpr = namedAlt.Expression;
        }

        // If the body is an inline block, produce an InlineBlockIR with null alt name
        if(bodyExpr is StormInlineBlockExpr inlineBlock)
        {
            var inlineBlockIR = LowerInlineBlock(inlineBlock, tokensByName, rulesByName, syntheticTokens, ruleDeclarations, diagnostics);
            return new RuleAlternativeIR(null, new List<FieldLabelIR>(), inlineBlockIR);
        }

        // Collect field labels
        var fieldLabels = new List<FieldLabelIR>();
        var loweredExpr = LowerRuleExpression(bodyExpr, tokensByName, rulesByName, syntheticTokens, ruleDeclarations, fieldLabels, diagnostics);

        return new RuleAlternativeIR(altName, fieldLabels, loweredExpr);
    }

    /// <summary>
    /// Recursively lowers a rule expression, resolving references to
    /// IR-level pointers and replacing anonymous string literals with
    /// synthetic token references.
    /// </summary>
    private static ExpressionIR LowerRuleExpression(
        StormExpression expr,
        Dictionary<string, TokenIR> tokensByName,
        Dictionary<string, RuleIR> rulesByName,
        Dictionary<string, TokenIR> syntheticTokens,
        Dictionary<string, StormDeclaration> ruleDeclarations,
        List<FieldLabelIR> fieldLabels,
        List<StormDiagnostic> diagnostics)
    {
        switch(expr)
        {
            case StormReferenceExpr refExpr:
                // Resolve to token or rule
                if(tokensByName.TryGetValue(refExpr.Name, out var tokenRef))
                {
                    return new TokenRefIR(tokenRef);
                }

                if(rulesByName.TryGetValue(refExpr.Name, out var ruleRef))
                {
                    return new RuleRefIR(ruleRef);
                }

                diagnostics.Add(new StormDiagnostic(
                    StormDiagnosticIds.STORM_UNDEFINED_REFERENCE,
                    StormDiagnosticSeverity.Error,
                    $"Cannot resolve reference '{refExpr.Name}' to a token or rule in IR lowering",
                    null, 0, 0));
                return new StringLiteralIR("");

            case StormQualifiedReferenceExpr qualRef:
                var qualName = qualRef.Qualifier + "." + qualRef.Name;
                if(tokensByName.TryGetValue(qualName, out var qualTokenRef))
                {
                    return new TokenRefIR(qualTokenRef);
                }

                if(rulesByName.TryGetValue(qualName, out var qualRuleRef))
                {
                    return new RuleRefIR(qualRuleRef);
                }

                diagnostics.Add(new StormDiagnostic(
                    StormDiagnosticIds.STORM_UNDEFINED_REFERENCE,
                    StormDiagnosticSeverity.Error,
                    $"Cannot resolve qualified reference '{qualName}' to a token or rule in IR lowering",
                    null, 0, 0));
                return new StringLiteralIR("");

            case StormStringLiteralExpr strExpr:
                var declaredToken = DeclaredTokenSpelling(strExpr.Value, tokensByName.Values);
                if(declaredToken != null)
                {
                    return new TokenRefIR(declaredToken);
                }

                // Replace with a reference to the synthetic token
                var synthName = SyntheticTokenName(strExpr.Value);
                if(syntheticTokens.TryGetValue(synthName, out var synthToken))
                {
                    return new TokenRefIR(synthToken);
                }

                if(tokensByName.TryGetValue(synthName, out var existingToken))
                {
                    return new TokenRefIR(existingToken);
                }
                // Should not happen if CollectAnonymousStringLiterals ran correctly
                diagnostics.Add(new StormDiagnostic(
                    StormDiagnosticIds.STORM_UNSUPPORTED_CONSTRUCT,
                    StormDiagnosticSeverity.Error,
                    $"Synthetic token for string literal '{strExpr.Value}' not found",
                    null, 0, 0));
                return new StringLiteralIR(strExpr.Value);

            case StormSequenceExpr seqExpr:
                var seqElements = new List<ExpressionIR>();
                foreach(var element in seqExpr.Elements)
                {
                    var loweredElement = LowerRuleExpression(element, tokensByName, rulesByName, syntheticTokens, ruleDeclarations, fieldLabels, diagnostics);

                    // The AST reads a group's values off its direct elements only.
                    if(loweredElement is SequenceIR inner && !fieldLabels.Any(label => ReferenceEquals(label.Expression, inner)))
                    {
                        seqElements.AddRange(inner.Elements);
                    }
                    else
                    {
                        seqElements.Add(loweredElement);
                    }
                }

                return new SequenceIR(seqElements);

            case StormAlternationExpr altExpr:
                var altElements = new List<ExpressionIR>();
                foreach(var alt in altExpr.Alternatives)
                {
                    altElements.Add(LowerRuleExpression(alt, tokensByName, rulesByName, syntheticTokens, ruleDeclarations, fieldLabels, diagnostics));
                }

                return new AlternationIR(altElements);

            case StormOptionalExpr optExpr:
                return new OptionalIR(LowerRuleExpression(optExpr.Inner, tokensByName, rulesByName, syntheticTokens, ruleDeclarations, fieldLabels, diagnostics));

            case StormZeroOrMoreExpr zomExpr:
                return new ZeroOrMoreIR(LowerRuleExpression(zomExpr.Inner, tokensByName, rulesByName, syntheticTokens, ruleDeclarations, fieldLabels, diagnostics));

            case StormOneOrMoreExpr oomExpr:
                return new OneOrMoreIR(LowerRuleExpression(oomExpr.Inner, tokensByName, rulesByName, syntheticTokens, ruleDeclarations, fieldLabels, diagnostics));

            case StormGroupExpr groupExpr:
                return LowerRuleExpression(groupExpr.Inner, tokensByName, rulesByName, syntheticTokens, ruleDeclarations, fieldLabels, diagnostics);

            case StormPredicateExpr predicateExpr:
                var predicateLabels = new List<FieldLabelIR>();
                var predicateInner = LowerRuleExpression(predicateExpr.Inner, tokensByName, rulesByName, syntheticTokens, ruleDeclarations, predicateLabels, diagnostics);
                if(predicateLabels.Count > 0)
                {
                    diagnostics.Add(new StormDiagnostic(
                        StormDiagnosticIds.STORM_UNSUPPORTED_CONSTRUCT,
                        StormDiagnosticSeverity.Error,
                        $"Label '{predicateLabels[0].Label}' inside a predicate: a predicate yields no AST child",
                        null, 0, 0));
                }

                if(predicateInner is not RuleRefIR && !IsFixedWidthTokenExpression(predicateInner))
                {
                    diagnostics.Add(new StormDiagnostic(
                        StormDiagnosticIds.STORM_UNSUPPORTED_CONSTRUCT,
                        StormDiagnosticSeverity.Error,
                        "A predicate body must be a rule reference or a fixed-width expression over tokens",
                        null, 0, 0));
                }

                return new PredicateIR(predicateInner, predicateExpr.IsNegated);

            case StormLabeledExpr labeledExpr:
                var labeledInner = LowerRuleExpression(labeledExpr.Element, tokensByName, rulesByName, syntheticTokens, ruleDeclarations, fieldLabels, diagnostics);
                fieldLabels.Add(new FieldLabelIR(labeledExpr.Label, labeledInner));
                return labeledInner;

            case StormNamedAlternative namedAlt:
                // Nested named alternatives shouldn't normally occur at this level,
                // but handle gracefully
                return LowerRuleExpression(namedAlt.Expression, tokensByName, rulesByName, syntheticTokens, ruleDeclarations, fieldLabels, diagnostics);

            case StormInlineBlockExpr inlineBlock:
                return LowerInlineBlock(inlineBlock, tokensByName, rulesByName, syntheticTokens, ruleDeclarations, diagnostics);

            case StormCharClassExpr ccExpr:
                // Char classes shouldn't appear in rule expressions, but handle gracefully
                return new CharClassIR(ccExpr.Text);

            case StormRegexLiteralExpr regexExpr:
                return new RegexLiteralIR(regexExpr.Pattern);

            default:
                diagnostics.Add(new StormDiagnostic(
                    StormDiagnosticIds.STORM_UNSUPPORTED_CONSTRUCT,
                    StormDiagnosticSeverity.Error,
                    $"Unsupported expression type in rule expression: {expr.GetType().Name}",
                    null, 0, 0));
                return new StringLiteralIR("");
        }
    }

    private static bool IsFixedWidthTokenExpression(ExpressionIR expr)
    {
        return TokenWidth(expr) >= 0;
    }

    private static int TokenWidth(ExpressionIR expr)
    {
        switch(expr)
        {
            case TokenRefIR:
                return 1;

            case SequenceIR sequence:
                var total = 0;
                foreach(var element in sequence.Elements)
                {
                    var width = TokenWidth(element);
                    if(width < 0)
                    {
                        return -1;
                    }

                    total += width;
                }

                return total;

            case AlternationIR alternation:
                var shared = -1;
                foreach(var alternative in alternation.Alternatives)
                {
                    var width = TokenWidth(alternative);
                    if(width < 0 || (shared >= 0 && width != shared))
                    {
                        return -1;
                    }

                    shared = width;
                }

                return shared;

            default:
                return -1;
        }
    }

    // ---------------------------------------------------------------
    //  Inline block lowering
    // ---------------------------------------------------------------

    /// <summary>
    /// Lowers a <see cref="StormInlineBlockExpr"/> into an <see cref="InlineBlockIR"/>.
    /// Resolves the prefix rule, extracts its body elements and labels,
    /// then for each arm strips the prefix reference from the arm rule's body
    /// and lowers the remaining tail elements.
    /// </summary>
    private static InlineBlockIR LowerInlineBlock(
        StormInlineBlockExpr inlineBlock,
        Dictionary<string, TokenIR> tokensByName,
        Dictionary<string, RuleIR> rulesByName,
        Dictionary<string, TokenIR> syntheticTokens,
        Dictionary<string, StormDeclaration> ruleDeclarations,
        List<StormDiagnostic> diagnostics)
    {
        // 1. Resolve the prefix rule and lower its body elements
        var prefixRuleName = ((StormReferenceExpr)inlineBlock.Prefix).Name;
        var prefixDecl = ruleDeclarations[prefixRuleName];
        var prefixLabels = new List<FieldLabelIR>();
        var prefixElements = LowerPrefixBody(prefixDecl.Body, tokensByName, rulesByName, syntheticTokens, ruleDeclarations, prefixLabels, diagnostics);

        // 2. Lower each arm
        var arms = new List<InlineArmIR>();
        foreach(var arm in inlineBlock.Arms)
        {
            // Handle nested inline blocks recursively
            if(arm.Expression is StormInlineBlockExpr nestedBlock)
            {
                var nestedIR = LowerInlineBlock(nestedBlock, tokensByName, rulesByName, syntheticTokens, ruleDeclarations, diagnostics);
                // For nested inline blocks, we wrap the nested InlineBlockIR as a
                // synthetic arm. The target rule doesn't exist in this case, so we
                // create a placeholder. In practice, nested inline blocks are processed
                // by the emitter differently.
                var nestedArm = new InlineArmIR(
                    arm.AlternativeName,
                    arm.Label,
                    rulesByName[((StormReferenceExpr)nestedBlock.Prefix).Name],
                    nestedIR,
                    new List<FieldLabelIR>());
                arms.Add(nestedArm);
                continue;
            }

            // The arm's Expression is a StormReferenceExpr naming the target rule
            var armRuleName = ((StormReferenceExpr)arm.Expression).Name;
            var armRuleIR = rulesByName[armRuleName];
            var armDecl = ruleDeclarations[armRuleName];

            // Get the arm rule's body sequence elements
            var tailLabels = new List<FieldLabelIR>();
            var tailExpression = LowerArmTail(armDecl.Body, prefixRuleName, prefixElements.Count, tokensByName, rulesByName, syntheticTokens, ruleDeclarations, tailLabels, diagnostics);

            var armIR = new InlineArmIR(
                arm.AlternativeName,
                arm.Label,
                armRuleIR,
                tailExpression,
                tailLabels);
            arms.Add(armIR);
        }

        return new InlineBlockIR(rulesByName[prefixRuleName], prefixElements, prefixLabels, arms);
    }

    /// <summary>
    /// Lowers the prefix rule's body into a list of IR elements.
    /// The prefix rule always has a single-sequence body (verified by InlinePrefixVerification).
    /// </summary>
    private static IReadOnlyList<ExpressionIR> LowerPrefixBody(
        StormExpression body,
        Dictionary<string, TokenIR> tokensByName,
        Dictionary<string, RuleIR> rulesByName,
        Dictionary<string, TokenIR> syntheticTokens,
        Dictionary<string, StormDeclaration> ruleDeclarations,
        List<FieldLabelIR> prefixLabels,
        List<StormDiagnostic> diagnostics)
    {
        var elements = new List<ExpressionIR>();

        if(body is StormSequenceExpr seq)
        {
            foreach(var element in seq.Elements)
            {
                elements.Add(LowerRuleExpression(element, tokensByName, rulesByName, syntheticTokens, ruleDeclarations, prefixLabels, diagnostics));
            }
        }
        else
        {
            // Single-element body (not wrapped in a sequence)
            elements.Add(LowerRuleExpression(body, tokensByName, rulesByName, syntheticTokens, ruleDeclarations, prefixLabels, diagnostics));
        }

        return elements;
    }

    /// <summary>
    /// Lowers an arm rule's body after stripping the leading prefix elements.
    /// If the arm rule starts with a reference to the prefix rule (possibly labeled),
    /// strip just that one element. Otherwise, strip elements by prefix count
    /// (for arm rules that have the prefix elements inline).
    /// </summary>
    private static ExpressionIR LowerArmTail(
        StormExpression body,
        string prefixRuleName,
        int prefixElementCount,
        Dictionary<string, TokenIR> tokensByName,
        Dictionary<string, RuleIR> rulesByName,
        Dictionary<string, TokenIR> syntheticTokens,
        Dictionary<string, StormDeclaration> ruleDeclarations,
        List<FieldLabelIR> tailLabels,
        List<StormDiagnostic> diagnostics)
    {
        // Determine how many leading elements to strip
        var stripCount = ComputeStripCount(body, prefixRuleName, prefixElementCount);

        if(body is StormSequenceExpr seq)
        {
            if(seq.Elements.Count <= stripCount)
            {
                // Only the prefix elements — empty tail
                return new SequenceIR(new List<ExpressionIR>());
            }

            var tailElements = new List<ExpressionIR>();
            for(var i = stripCount; i < seq.Elements.Count; i++)
            {
                tailElements.Add(LowerRuleExpression(seq.Elements[i], tokensByName, rulesByName, syntheticTokens, ruleDeclarations, tailLabels, diagnostics));
            }

            if(tailElements.Count == 1)
            {
                return tailElements[0];
            }

            return new SequenceIR(tailElements);
        }
        else
        {
            // Single-element body — if strip count >= 1, it's all prefix
            if(stripCount >= 1)
            {
                return new SequenceIR(new List<ExpressionIR>());
            }

            return LowerRuleExpression(body, tokensByName, rulesByName, syntheticTokens, ruleDeclarations, tailLabels, diagnostics);
        }
    }

    /// <summary>
    /// Determines how many leading elements to strip from the arm rule's body.
    /// If the first element is a reference to the prefix rule (possibly labeled),
    /// strip just 1 element. Otherwise, strip by prefix element count (inline prefix).
    /// </summary>
    private static int ComputeStripCount(StormExpression body, string prefixRuleName, int prefixElementCount)
    {
        StormExpression? firstElement = null;

        if(body is StormSequenceExpr seq)
        {
            if(seq.Elements.Count > 0)
            {
                firstElement = seq.Elements[0];
            }
        }
        else
        {
            firstElement = body;
        }

        if(firstElement == null)
        {
            return prefixElementCount;
        }

        // Unwrap label if present
        if(firstElement is StormLabeledExpr labeled)
        {
            firstElement = labeled.Element;
        }

        // Check if the first element is a reference to the prefix rule
        if(firstElement is StormReferenceExpr refExpr && refExpr.Name == prefixRuleName)
        {
            return 1; // Strip just the rule reference
        }

        // Otherwise, the prefix elements are inline — strip by count
        return prefixElementCount;
    }
}