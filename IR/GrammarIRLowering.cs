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
///   <item>Pattern inlining — replaces fragment and token references in token
///         expressions with the named declaration's body expression.</item>
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
        var identifiers = EmittedNames.Of(grammar);

        // ---------------------------------------------------------------
        // 0. Scan for StormModeDecl nodes to build mode name list and
        //    a mapping from declaration name → enclosing mode name.
        // ---------------------------------------------------------------
        var modeNames = grammar.ModeNames.Select(mode => EmittedNames.Of(identifiers, mode)).ToList();
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
        // 1. Collect fragments and tokens, whose patterns a token
        //    expression inlines where it names them (mode-scoped)
        // ---------------------------------------------------------------
        var patternsByName = new Dictionary<(string Mode, string Name), StormDeclaration>();
        foreach(var decl in grammar.Declarations)
        {
            if(decl is StormFragmentDecl or StormTokenDecl or StormHiddenTokenDecl)
            {
                var mode = declNameToMode.TryGetValue(decl.Name, out var m) ? m : "default";
                patternsByName[(mode, decl.Name)] = decl;
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
                var modeAction = ConvertModeAction(decl, identifiers);
                var inlinedExpr = InlinePatterns(decl.Body, patternsByName, mode, new HashSet<string>(StringComparer.Ordinal), diagnostics);
                var tokenIR = new TokenIR(EmittedNames.Of(identifiers, decl.Name), inlinedExpr, isHidden, tokenIndex, mode: EmittedNames.Of(identifiers, mode), modeAction: modeAction, displayName: decl.Name);
                tokenIRs.Add(tokenIR);
                tokensByName[decl.Name] = tokenIR;
                tokenIndex++;
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
                var shell = new RuleIR(EmittedNames.Of(identifiers, decl.Name), new List<RuleAlternativeIR>(), isMemo, displayName: decl.Name);
                ruleShells[decl.Name] = shell;
                ruleOrder.Add((decl.Name, decl));
            }
        }

        // ---------------------------------------------------------------
        // 4. First pass over rule expressions: collect anonymous string
        //    literals and create synthetic tokens for them
        // ---------------------------------------------------------------
        var literals = new LiteralTokens(tokenIRs);

        foreach(var (name, decl) in ruleOrder)
        {
            CollectAnonymousStringLiterals(decl.Body, literals, tokensByName, ref tokenIndex);
        }

        // Append synthetic tokens to the token list
        foreach(var synth in literals.Synthetic.Values)
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
            var alternatives = LowerRuleAlternatives(decl.Body, tokensByName, ruleShells, literals, ruleDeclarations, diagnostics);
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
    /// Recursively inlines fragment and token references in a token expression.
    /// They are resolved first within the current mode, then falling
    /// back to the default mode.
    /// </summary>
    private static ExpressionIR InlinePatterns(
        StormExpression expr,
        Dictionary<(string Mode, string Name), StormDeclaration> patternsByName,
        string currentMode,
        HashSet<string> inliningStack,
        List<StormDiagnostic> diagnostics)
    {
        switch(expr)
        {
            case StormReferenceExpr refExpr:
                if(TryResolvePattern(patternsByName, currentMode, refExpr.Name, out var pattern))
                {
                    if(!inliningStack.Add(refExpr.Name))
                    {
                        diagnostics.Add(new StormDiagnostic(
                            StormDiagnosticIds.STORM_UNSUPPORTED_CONSTRUCT,
                            StormDiagnosticSeverity.Error,
                            $"Circular pattern reference detected: '{refExpr.Name}'",
                            null, 0, 0));
                        return new StringLiteralIR(""); // placeholder
                    }

                    var inlined = InlinePatterns(pattern.Body, patternsByName, currentMode, inliningStack, diagnostics);
                    _ = inliningStack.Remove(refExpr.Name);
                    return new InlineFragmentIR(refExpr.Name, inlined);
                }

                return Uninlinable(refExpr.Name, currentMode, diagnostics);

            case StormQualifiedReferenceExpr qualRef:
                var qualName = qualRef.Qualifier + "." + qualRef.Name;
                if(TryResolvePattern(patternsByName, currentMode, qualName, out var qualPattern))
                {
                    if(!inliningStack.Add(qualName))
                    {
                        diagnostics.Add(new StormDiagnostic(
                            StormDiagnosticIds.STORM_UNSUPPORTED_CONSTRUCT,
                            StormDiagnosticSeverity.Error,
                            $"Circular pattern reference detected: '{qualName}'",
                            null, 0, 0));
                        return new StringLiteralIR("");
                    }

                    var inlined = InlinePatterns(qualPattern.Body, patternsByName, currentMode, inliningStack, diagnostics);
                    _ = inliningStack.Remove(qualName);
                    return new InlineFragmentIR(qualName, inlined);
                }

                return Uninlinable(qualName, currentMode, diagnostics);

            case StormSequenceExpr seqExpr:
                var seqElements = new List<ExpressionIR>();
                foreach(var element in seqExpr.Elements)
                {
                    seqElements.Add(InlinePatterns(element, patternsByName, currentMode, inliningStack, diagnostics));
                }

                return new SequenceIR(seqElements);

            case StormAlternationExpr altExpr:
                var altElements = new List<ExpressionIR>();
                foreach(var alt in altExpr.Alternatives)
                {
                    altElements.Add(InlinePatterns(alt, patternsByName, currentMode, inliningStack, diagnostics));
                }

                return new AlternationIR(altElements);

            case StormOptionalExpr optExpr:
                return new OptionalIR(InlinePatterns(optExpr.Inner, patternsByName, currentMode, inliningStack, diagnostics));

            case StormZeroOrMoreExpr zomExpr:
                return new ZeroOrMoreIR(InlinePatterns(zomExpr.Inner, patternsByName, currentMode, inliningStack, diagnostics));

            case StormOneOrMoreExpr oomExpr:
                return new OneOrMoreIR(InlinePatterns(oomExpr.Inner, patternsByName, currentMode, inliningStack, diagnostics));

            case StormGroupExpr groupExpr:
                return InlinePatterns(groupExpr.Inner, patternsByName, currentMode, inliningStack, diagnostics);

            case StormStringLiteralExpr strExpr:
                return new StringLiteralIR(strExpr.Value);

            case StormCharClassExpr ccExpr:
                return new CharClassIR(ccExpr.Text);

            case StormRegexLiteralExpr regexExpr:
                return new RegexLiteralIR(regexExpr.Pattern);

            case StormLabeledExpr labeledExpr:
                // Labels don't appear in token expressions normally, but handle gracefully
                return InlinePatterns(labeledExpr.Element, patternsByName, currentMode, inliningStack, diagnostics);

            case StormNamedAlternative namedAlt:
                // Named alternatives don't appear in token expressions, but handle gracefully
                return InlinePatterns(namedAlt.Expression, patternsByName, currentMode, inliningStack, diagnostics);

            default:
                diagnostics.Add(new StormDiagnostic(
                    StormDiagnosticIds.STORM_UNSUPPORTED_CONSTRUCT,
                    StormDiagnosticSeverity.Error,
                    $"Unsupported expression type in token expression: {expr.GetType().Name}",
                    null, 0, 0));
                return new StringLiteralIR("");
        }
    }

    private static ExpressionIR Uninlinable(string name, string mode, List<StormDiagnostic> diagnostics)
    {
        diagnostics.Add(new StormDiagnostic(
            StormDiagnosticIds.STORM_UNSUPPORTED_CONSTRUCT,
            StormDiagnosticSeverity.Error,
            $"Token pattern '{name}' cannot be inlined into a token of mode '{mode}'",
            null, 0, 0));
        return new StringLiteralIR(name);
    }

    /// <summary>
    /// Resolves a fragment or token by name, first looking in the current mode,
    /// then falling back to the default mode.
    /// </summary>
    private static bool TryResolvePattern(
        Dictionary<(string Mode, string Name), StormDeclaration> patternsByName,
        string currentMode,
        string name,
        out StormDeclaration pattern)
    {
        // Try current mode first
        if(patternsByName.TryGetValue((currentMode, name), out pattern!))
        {
            return true;
        }

        // Fall back to default mode if not already in default
        if(currentMode != "default" && patternsByName.TryGetValue(("default", name), out pattern!))
        {
            return true;
        }

        pattern = null!;
        return false;
    }

    /// <summary>
    /// Converts a <see cref="StormModeAction"/> from the AST to a <see cref="ModeActionIR"/>.
    /// </summary>
    private static ModeActionIR ConvertModeAction(StormDeclaration decl, IReadOnlyDictionary<string, string> identifiers)
    {
        var action = decl switch
        {
            StormTokenDecl tokenDecl => tokenDecl.Action,
            StormHiddenTokenDecl hiddenDecl => hiddenDecl.Action,
            _ => null
        };

        return action switch
        {
            StormPushAction pushAction => new PushModeIR(EmittedNames.Of(identifiers, pushAction.ModeName)),
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
    /// that no declared token accepts.
    /// </summary>
    private static void CollectAnonymousStringLiterals(
        StormExpression expr,
        LiteralTokens literals,
        Dictionary<string, TokenIR> existingTokens,
        ref int tokenIndex)
    {
        switch(expr)
        {
            case StormStringLiteralExpr strExpr:
                var synthName = SyntheticTokenName(strExpr.Value);
                if(literals.Lexed(strExpr.Value) == null && !literals.Synthetic.ContainsKey(synthName) && !existingTokens.ContainsKey(synthName))
                {
                    var synthToken = new TokenIR(
                        synthName,
                        new StringLiteralIR(strExpr.Value),
                        isHidden: false,
                        declarationIndex: tokenIndex,
                        isSynthetic: true);
                    literals.Synthetic[synthName] = synthToken;
                    tokenIndex++;
                }

                break;

            case StormSequenceExpr seqExpr:
                foreach(var element in seqExpr.Elements)
                {
                    CollectAnonymousStringLiterals(element, literals, existingTokens, ref tokenIndex);
                }

                break;

            case StormAlternationExpr altExpr:
                foreach(var alt in altExpr.Alternatives)
                {
                    CollectAnonymousStringLiterals(alt, literals, existingTokens, ref tokenIndex);
                }

                break;

            case StormOptionalExpr optExpr:
                CollectAnonymousStringLiterals(optExpr.Inner, literals, existingTokens, ref tokenIndex);
                break;

            case StormZeroOrMoreExpr zomExpr:
                CollectAnonymousStringLiterals(zomExpr.Inner, literals, existingTokens, ref tokenIndex);
                break;

            case StormOneOrMoreExpr oomExpr:
                CollectAnonymousStringLiterals(oomExpr.Inner, literals, existingTokens, ref tokenIndex);
                break;

            case StormGroupExpr groupExpr:
                CollectAnonymousStringLiterals(groupExpr.Inner, literals, existingTokens, ref tokenIndex);
                break;

            case StormPredicateExpr predicateExpr:
                CollectAnonymousStringLiterals(predicateExpr.Inner, literals, existingTokens, ref tokenIndex);
                break;

            case StormLabeledExpr labeledExpr:
                CollectAnonymousStringLiterals(labeledExpr.Element, literals, existingTokens, ref tokenIndex);
                break;

            case StormNamedAlternative namedAlt:
                CollectAnonymousStringLiterals(namedAlt.Expression, literals, existingTokens, ref tokenIndex);
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
                            CollectAnonymousStringLiterals(nestedBlock, literals, existingTokens, ref tokenIndex);
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
        LiteralTokens literals,
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
                alternatives.Add(LowerSingleAlternative(alt, tokensByName, rulesByName, literals, ruleDeclarations, diagnostics));
            }
        }
        else
        {
            alternatives.Add(LowerSingleAlternative(body, tokensByName, rulesByName, literals, ruleDeclarations, diagnostics));
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
        LiteralTokens literals,
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
            var inlineBlockIR = LowerInlineBlock(inlineBlock, tokensByName, rulesByName, literals, ruleDeclarations, diagnostics);
            return new RuleAlternativeIR(null, new List<FieldLabelIR>(), inlineBlockIR);
        }

        // Collect field labels
        var fieldLabels = new List<FieldLabelIR>();
        var loweredExpr = LowerRuleExpression(bodyExpr, tokensByName, rulesByName, literals, ruleDeclarations, fieldLabels, diagnostics);

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
        LiteralTokens literals,
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
                if(literals.Lexed(strExpr.Value) is { } lexed)
                {
                    return lexed;
                }

                // Replace with a reference to the synthetic token
                var synthName = SyntheticTokenName(strExpr.Value);
                if(literals.Synthetic.TryGetValue(synthName, out var synthToken))
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
                    var loweredElement = LowerRuleExpression(element, tokensByName, rulesByName, literals, ruleDeclarations, fieldLabels, diagnostics);

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
                    altElements.Add(LowerRuleExpression(alt, tokensByName, rulesByName, literals, ruleDeclarations, fieldLabels, diagnostics));
                }

                return new AlternationIR(altElements);

            case StormOptionalExpr optExpr:
                return new OptionalIR(LowerRuleExpression(optExpr.Inner, tokensByName, rulesByName, literals, ruleDeclarations, fieldLabels, diagnostics));

            case StormZeroOrMoreExpr zomExpr:
                return new ZeroOrMoreIR(LowerRuleExpression(zomExpr.Inner, tokensByName, rulesByName, literals, ruleDeclarations, fieldLabels, diagnostics));

            case StormOneOrMoreExpr oomExpr:
                return new OneOrMoreIR(LowerRuleExpression(oomExpr.Inner, tokensByName, rulesByName, literals, ruleDeclarations, fieldLabels, diagnostics));

            case StormGroupExpr groupExpr:
                return LowerRuleExpression(groupExpr.Inner, tokensByName, rulesByName, literals, ruleDeclarations, fieldLabels, diagnostics);

            case StormPredicateExpr predicateExpr:
                var predicateLabels = new List<FieldLabelIR>();
                var predicateInner = LowerRuleExpression(predicateExpr.Inner, tokensByName, rulesByName, literals, ruleDeclarations, predicateLabels, diagnostics);
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
                var labeledInner = LowerRuleExpression(labeledExpr.Element, tokensByName, rulesByName, literals, ruleDeclarations, fieldLabels, diagnostics);
                fieldLabels.Add(new FieldLabelIR(labeledExpr.Label, labeledInner));
                return labeledInner;

            case StormNamedAlternative namedAlt:
                // Nested named alternatives shouldn't normally occur at this level,
                // but handle gracefully
                return LowerRuleExpression(namedAlt.Expression, tokensByName, rulesByName, literals, ruleDeclarations, fieldLabels, diagnostics);

            case StormInlineBlockExpr inlineBlock:
                return LowerInlineBlock(inlineBlock, tokensByName, rulesByName, literals, ruleDeclarations, diagnostics);

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
        LiteralTokens literals,
        Dictionary<string, StormDeclaration> ruleDeclarations,
        List<StormDiagnostic> diagnostics)
    {
        // 1. Resolve the prefix rule and lower its body elements
        var prefixRuleName = ((StormReferenceExpr)inlineBlock.Prefix).Name;
        var prefixDecl = ruleDeclarations[prefixRuleName];
        var prefixLabels = new List<FieldLabelIR>();
        var prefixElements = LowerPrefixBody(prefixDecl.Body, tokensByName, rulesByName, literals, ruleDeclarations, prefixLabels, diagnostics);

        // 2. Lower each arm
        var arms = new List<InlineArmIR>();
        foreach(var arm in inlineBlock.Arms)
        {
            // Handle nested inline blocks recursively
            if(arm.Expression is StormInlineBlockExpr nestedBlock)
            {
                var nestedIR = LowerInlineBlock(nestedBlock, tokensByName, rulesByName, literals, ruleDeclarations, diagnostics);
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
            var tailExpression = LowerArmTail(armDecl.Body, prefixRuleName, prefixElements.Count, tokensByName, rulesByName, literals, ruleDeclarations, tailLabels, diagnostics);

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
        LiteralTokens literals,
        Dictionary<string, StormDeclaration> ruleDeclarations,
        List<FieldLabelIR> prefixLabels,
        List<StormDiagnostic> diagnostics)
    {
        var elements = new List<ExpressionIR>();

        if(body is StormSequenceExpr seq)
        {
            foreach(var element in seq.Elements)
            {
                elements.Add(LowerRuleExpression(element, tokensByName, rulesByName, literals, ruleDeclarations, prefixLabels, diagnostics));
            }
        }
        else
        {
            // Single-element body (not wrapped in a sequence)
            elements.Add(LowerRuleExpression(body, tokensByName, rulesByName, literals, ruleDeclarations, prefixLabels, diagnostics));
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
        LiteralTokens literals,
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
                tailElements.Add(LowerRuleExpression(seq.Elements[i], tokensByName, rulesByName, literals, ruleDeclarations, tailLabels, diagnostics));
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

            return LowerRuleExpression(body, tokensByName, rulesByName, literals, ruleDeclarations, tailLabels, diagnostics);
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