using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Property-based tests (FsCheck) for the anonymous-group projection feature in the
/// Storm source generator (spec: storm-anonymous-groups, design "Correctness
/// Properties" 1-9, Requirement 9).
///
/// Each test builds <see cref="RuleIR"/> / <see cref="RuleAlternativeIR"/> /
/// <see cref="ExpressionIR"/> / <see cref="GrammarIR"/> DIRECTLY in memory (no
/// <c>.storm</c> text is parsed, no C# is compiled, no process is spawned) and invokes
/// <see cref="AstEmitter.EmitAstNodes(EmissionContext)"/> or
/// <see cref="ParserEmitter.Emit(EmissionContext)"/> in-process, asserting on the
/// emitted string. Reference identity of each <see cref="FieldLabelIR.Expression"/> is
/// preserved by wiring the same instance into the tree and the label list, matching how
/// the lowering stage links labels to expressions.
///
/// Randomness is driven by FsCheck: a generator produces a seed, and a deterministic
/// builder turns that seed into a varied-but-valid IR shape (random rule/label/token
/// identifiers drawn distinctly from fixed pools, random modifiers, random value-bearing
/// element kinds and counts, optional structural tokens). Every property runs at least
/// <see cref="Iterations"/> iterations. Because a failing seed reproduces the exact input,
/// failure messages carry the seed and a shape description.
///
/// Capture-count rule under test: a group projects a <c>{Rule}NodeGroup{N}</c> struct IFF
/// it holds two or more value-bearing elements (a labelled element or an unlabelled rule
/// reference; bare fixed tokens are structural and are not counted).
///
/// No-modifier note (reported to the orchestrator): the committed
/// <see cref="AstEmitter.DetectAnonymousGroups"/> only reports groups wrapped in
/// <c>*</c>, <c>+</c>, or <c>?</c> -- a parenthesised group with NO modifier is never
/// struct-projected (see <c>ParserGroupConstructionTests</c> Task 8.4). Design Property 3
/// lists a "no modifier -> scalar struct" case, but that shape is not produced by the
/// committed detection, so Property 3 here exercises the three modifiers that actually
/// project (<c>?</c>/<c>*</c>/<c>+</c>) rather than asserting a behaviour the generator
/// intentionally does not implement.
/// </summary>
public class StormAnonymousGroupsPropertyTests
{
    private const int Iterations = 100;

    // Fixed identifier pools. Rule names (PascalCase) and label names (lower-case whose
    // PascalCase form) are deliberately disjoint so a labelled field never collides with
    // an unlabelled rule-reference field. Draws are distinct within a single sample (a
    // NameCursor advances through the pool), so struct field names never need the
    // emitter's EnsureUnique "2" suffix and assertions stay deterministic.
    private static readonly string[] RulePool =
    {
        "Alfa", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf", "Hotel",
        "India", "Juliet", "Kilo", "Lima", "Mike", "November", "Oscar", "Papa",
        "Quebec", "Romeo", "Sierra", "Tango", "Uniform", "Victor", "Whiskey", "Xray",
        "Yankee", "Zulu", "Anchor", "Beacon", "Cinder", "Dagger", "Ember", "Flint",
    };

    private static readonly string[] LabelPool =
    {
        "left", "right", "op", "operator", "head", "tail", "first", "second",
        "lhs", "rhs", "body", "cond", "name", "key", "val", "target",
        "source", "start", "stop", "lo", "hi", "inner", "outer", "pre",
        "post", "mid", "prefix", "suffix", "item", "slot", "part", "seg",
    };

    private static readonly string[] TokenPool =
    {
        "PLUS", "MINUS", "STAR", "SLASH", "PERCENT", "AMP", "PIPE", "CARET",
        "TILDE", "BANG", "QUESTION", "EQ", "NEQ", "LT", "GT", "LE",
        "GE", "DOT", "COMMA", "COLON", "SEMI", "LPAREN", "RPAREN", "LBRACE",
        "RBRACE", "LBRACKET", "RBRACKET", "ARROW", "AT", "HASH", "DOLLAR", "BACKSLASH",
    };

    // The repetition/optional modifier applied to an anonymous group. Bare (no-modifier)
    // groups are intentionally excluded: the committed detector never projects them.
    private enum Mod
    {
        Optional,
        Star,
        Plus,
    }

    // The kind of one value-bearing element inside a group. Every kind is value-bearing:
    // a labelled token, a labelled pure-token alternation (collapses to Token), a
    // labelled rule reference, or an unlabelled rule reference (named from the rule).
    private enum VbKind
    {
        LabeledToken,
        LabeledTokenAlt,
        LabeledRuleRef,
        UnlabeledRuleRef,
    }

    // A backward-compatibility shape (Property 7): none of these project a group struct.
    private enum BackShape
    {
        TokenOnlyRepetition,
        RuleRefRepetition,
        RuleRefOptional,
        NoGroupFlat,
    }

    // -- Expectation model -------------------------------------------

    private sealed class ExpectedField
    {
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
        public bool IsNode { get; init; }
        public bool IsToken { get; init; }
        public bool IsLabeled { get; init; }
        public string? RuleName { get; init; }
    }

    private sealed class ExpectedGroup
    {
        public int Index { get; init; }
        public Mod Modifier { get; init; }
        public string StructName { get; init; } = "";
        public string PropertyName { get; init; } = "";
        public string PropertyType { get; init; } = "";
        public IReadOnlyList<ExpectedField> Fields { get; init; } = new List<ExpectedField>();
    }

    private sealed class BuiltRule
    {
        public int Seed { get; set; }
        public RuleIR Rule { get; init; } = null!;
        public RuleAlternativeIR Alt { get; init; } = null!;
        public string RuleName { get; init; } = "";
        public string NodeName { get; init; } = "";
        public IReadOnlyList<ExpectedGroup> Groups { get; init; } = new List<ExpectedGroup>();
        public string? LeadingFieldName { get; init; }
        public IReadOnlyList<TokenIR> Tokens { get; init; } = new List<TokenIR>();
    }

    private sealed class BackCompatBuilt
    {
        public int Seed { get; set; }
        public RuleIR Rule { get; init; } = null!;
        public RuleAlternativeIR Alt { get; init; } = null!;
        public string NodeName { get; init; } = "";
        public string ClassicSubstring { get; init; } = "";
        public string Shape { get; init; } = "";
        public IReadOnlyList<TokenIR> Tokens { get; init; } = new List<TokenIR>();
    }

    private sealed class LoneAltBuilt
    {
        public int Seed { get; set; }
        public RuleIR Rule { get; init; } = null!;
        public RuleAlternativeIR Alt { get; init; } = null!;
        public string NodeName { get; init; } = "";
        public string LabelName { get; init; } = "";
        public Mod Modifier { get; init; }
        public IReadOnlyList<TokenIR> Tokens { get; init; } = new List<TokenIR>();
    }

    private sealed record GroupSpec(Mod Modifier, IReadOnlyList<VbKind> Kinds, bool StructuralToken, int AltSize);

    // Advances through a fixed pool, wrapping modularly. Consecutive draws within a
    // sample are distinct because the number of draws is far below the pool size.
    private sealed class NameCursor
    {
        private readonly IReadOnlyList<string> pool;
        private int index;

        public NameCursor(IReadOnlyList<string> namePool, int startIndex)
        {
            pool = namePool;
            index = startIndex;
        }

        public string Next()
        {
            var name = pool[index % pool.Count];
            index++;
            return name;
        }
    }

    // -- IR builders -------------------------------------------------

    private static BuiltRule BuildRule(Random rng, bool includeLeading, IReadOnlyList<GroupSpec> specs)
    {
        var ruleCursor = new NameCursor(RulePool, rng.Next(RulePool.Length));
        var labelCursor = new NameCursor(LabelPool, rng.Next(LabelPool.Length));
        var tokenCursor = new NameCursor(TokenPool, rng.Next(TokenPool.Length));

        var ruleName = ruleCursor.Next();
        var nodeName = ruleName + "Node";

        var labels = new List<FieldLabelIR>();
        var topElements = new List<ExpressionIR>();
        var tokens = new List<TokenIR>();
        var tokenIndex = 0;

        TokenIR makeToken()
        {
            var token = new TokenIR(tokenCursor.Next(), new StringLiteralIR("x"), false, tokenIndex);
            tokenIndex++;
            tokens.Add(token);
            return token;
        }

        string? leadingFieldName = null;
        if(includeLeading)
        {
            var leadRuleName = ruleCursor.Next();
            var leadLabel = labelCursor.Next();
            var leadExpr = new RuleRefIR(new RuleIR(leadRuleName, new List<RuleAlternativeIR>()));
            labels.Add(new FieldLabelIR(leadLabel, leadExpr));
            topElements.Add(leadExpr);
            leadingFieldName = PascalCase(leadLabel);
        }

        var groups = new List<ExpectedGroup>();
        var groupIndex = 0;
        foreach(var spec in specs)
        {
            var seqElements = new List<ExpressionIR>();
            var fields = new List<ExpectedField>();

            if(spec.StructuralToken)
            {
                // A bare fixed token is structural: consumed by the parser but not stored,
                // so it contributes no struct field.
                seqElements.Add(new TokenRefIR(makeToken()));
            }

            foreach(var kind in spec.Kinds)
            {
                switch(kind)
                {
                    case VbKind.LabeledToken:
                        {
                            var label = labelCursor.Next();
                            var expr = new TokenRefIR(makeToken());
                            labels.Add(new FieldLabelIR(label, expr));
                            seqElements.Add(expr);
                            fields.Add(new ExpectedField { Name = PascalCase(label), Type = "Token", IsToken = true, IsLabeled = true });
                            break;
                        }

                    case VbKind.LabeledTokenAlt:
                        {
                            var label = labelCursor.Next();
                            var alternatives = new List<ExpressionIR>();
                            for(var i = 0; i < spec.AltSize; i++)
                            {
                                alternatives.Add(new TokenRefIR(makeToken()));
                            }

                            var expr = new AlternationIR(alternatives);
                            labels.Add(new FieldLabelIR(label, expr));
                            seqElements.Add(expr);
                            fields.Add(new ExpectedField { Name = PascalCase(label), Type = "Token", IsToken = true, IsLabeled = true });
                            break;
                        }

                    case VbKind.LabeledRuleRef:
                        {
                            var label = labelCursor.Next();
                            var refRuleName = ruleCursor.Next();
                            var expr = new RuleRefIR(new RuleIR(refRuleName, new List<RuleAlternativeIR>()));
                            labels.Add(new FieldLabelIR(label, expr));
                            seqElements.Add(expr);
                            fields.Add(new ExpectedField { Name = PascalCase(label), Type = refRuleName + "Node", IsNode = true, IsLabeled = true, RuleName = refRuleName });
                            break;
                        }

                    case VbKind.UnlabeledRuleRef:
                        {
                            var refRuleName = ruleCursor.Next();
                            var expr = new RuleRefIR(new RuleIR(refRuleName, new List<RuleAlternativeIR>()));
                            seqElements.Add(expr);
                            fields.Add(new ExpectedField { Name = PascalCase(refRuleName), Type = refRuleName + "Node", IsNode = true, IsLabeled = false, RuleName = refRuleName });
                            break;
                        }

                    default:
                        break;
                }
            }

            var seq = new SequenceIR(seqElements);
            topElements.Add(Wrap(spec.Modifier, seq));

            var structName = nodeName + "Group" + groupIndex;
            var propType = spec.Modifier == Mod.Optional ? structName + "?" : "ImmutableArray<" + structName + ">";
            groups.Add(new ExpectedGroup
            {
                Index = groupIndex,
                Modifier = spec.Modifier,
                StructName = structName,
                PropertyName = "Group" + groupIndex,
                PropertyType = propType,
                Fields = fields,
            });
            groupIndex++;
        }

        var topExpr = topElements.Count == 1 ? topElements[0] : new SequenceIR(topElements);
        var alt = new RuleAlternativeIR(null, labels, topExpr);
        return new BuiltRule
        {
            Rule = new RuleIR(ruleName, new List<RuleAlternativeIR> { alt }),
            Alt = alt,
            RuleName = ruleName,
            NodeName = nodeName,
            Groups = groups,
            LeadingFieldName = leadingFieldName,
            Tokens = tokens,
        };
    }

    // Builds a lone labelled pure-token alternation group -- e.g. (op:(PLUS | MINUS))* --
    // which holds a single value-bearing element and therefore is NOT struct-projected.
    private static LoneAltBuilt BuildLoneAlt(Random rng)
    {
        var ruleCursor = new NameCursor(RulePool, rng.Next(RulePool.Length));
        var labelCursor = new NameCursor(LabelPool, rng.Next(LabelPool.Length));
        var tokenCursor = new NameCursor(TokenPool, rng.Next(TokenPool.Length));

        var ruleName = ruleCursor.Next();
        var nodeName = ruleName + "Node";
        var modifier = RandomMod(rng);
        var includeLeading = rng.Next(2) == 0;
        var altSize = rng.Next(2, 4);

        var labels = new List<FieldLabelIR>();
        var topElements = new List<ExpressionIR>();
        var tokens = new List<TokenIR>();

        if(includeLeading)
        {
            var leadRuleName = ruleCursor.Next();
            var leadLabel = labelCursor.Next();
            var leadExpr = new RuleRefIR(new RuleIR(leadRuleName, new List<RuleAlternativeIR>()));
            labels.Add(new FieldLabelIR(leadLabel, leadExpr));
            topElements.Add(leadExpr);
        }

        var alternatives = new List<ExpressionIR>();
        for(var i = 0; i < altSize; i++)
        {
            var token = new TokenIR(tokenCursor.Next(), new StringLiteralIR("x"), false, i);
            tokens.Add(token);
            alternatives.Add(new TokenRefIR(token));
        }

        var altLabel = labelCursor.Next();
        var altExpr = new AlternationIR(alternatives);
        labels.Add(new FieldLabelIR(altLabel, altExpr));
        topElements.Add(Wrap(modifier, altExpr));

        var topExpr = topElements.Count == 1 ? topElements[0] : new SequenceIR(topElements);
        var alt = new RuleAlternativeIR(null, labels, topExpr);
        return new LoneAltBuilt
        {
            Rule = new RuleIR(ruleName, new List<RuleAlternativeIR> { alt }),
            Alt = alt,
            NodeName = nodeName,
            LabelName = PascalCase(altLabel),
            Modifier = modifier,
            Tokens = tokens,
        };
    }

    // Builds a sub-two-value-bearing (or no-group) shape that must NOT project a group
    // struct and must retain its classic promotion.
    private static BackCompatBuilt BuildBackCompat(Random rng)
    {
        var ruleCursor = new NameCursor(RulePool, rng.Next(RulePool.Length));
        var labelCursor = new NameCursor(LabelPool, rng.Next(LabelPool.Length));
        var tokenCursor = new NameCursor(TokenPool, rng.Next(TokenPool.Length));

        var ruleName = ruleCursor.Next();
        var nodeName = ruleName + "Node";

        var labels = new List<FieldLabelIR>();
        var topElements = new List<ExpressionIR>();
        var tokens = new List<TokenIR>();
        var tokenIndex = 0;

        TokenIR makeToken()
        {
            var token = new TokenIR(tokenCursor.Next(), new StringLiteralIR("x"), false, tokenIndex);
            tokenIndex++;
            tokens.Add(token);
            return token;
        }

        var shapes = new[]
        {
            BackShape.TokenOnlyRepetition,
            BackShape.RuleRefRepetition,
            BackShape.RuleRefOptional,
            BackShape.NoGroupFlat,
        };
        var shape = shapes[rng.Next(shapes.Length)];
        string classic;

        switch(shape)
        {
            case BackShape.TokenOnlyRepetition:
                {
                    var separator = new TokenRefIR(makeToken());
                    var ident = new TokenRefIR(makeToken());
                    var seq = new SequenceIR(new List<ExpressionIR> { separator, ident });
                    topElements.Add(rng.Next(2) == 0 ? new ZeroOrMoreIR(seq) : new OneOrMoreIR(seq));
                    classic = "ImmutableArray<Token>";
                    break;
                }

            case BackShape.RuleRefRepetition:
                {
                    var separator = new TokenRefIR(makeToken());
                    var leaf = ruleCursor.Next();
                    var seq = new SequenceIR(new List<ExpressionIR> { separator, new RuleRefIR(new RuleIR(leaf, new List<RuleAlternativeIR>())) });
                    topElements.Add(rng.Next(2) == 0 ? new ZeroOrMoreIR(seq) : new OneOrMoreIR(seq));
                    classic = "ImmutableArray<" + leaf + "Node>";
                    break;
                }

            case BackShape.RuleRefOptional:
                {
                    var separator = new TokenRefIR(makeToken());
                    var leaf = ruleCursor.Next();
                    var seq = new SequenceIR(new List<ExpressionIR> { separator, new RuleRefIR(new RuleIR(leaf, new List<RuleAlternativeIR>())) });
                    topElements.Add(new OptionalIR(seq));
                    classic = leaf + "Node?";
                    break;
                }

            default:
                {
                    // NoGroupFlat: head:Leaf op:TOKEN tail:Leaf2 -- labelled, no repetition or
                    // optional, so no anonymous group exists at all.
                    var leaf1 = ruleCursor.Next();
                    var label1 = labelCursor.Next();
                    var expr1 = new RuleRefIR(new RuleIR(leaf1, new List<RuleAlternativeIR>()));
                    labels.Add(new FieldLabelIR(label1, expr1));
                    topElements.Add(expr1);

                    var label2 = labelCursor.Next();
                    var expr2 = new TokenRefIR(makeToken());
                    labels.Add(new FieldLabelIR(label2, expr2));
                    topElements.Add(expr2);

                    var leaf2 = ruleCursor.Next();
                    var label3 = labelCursor.Next();
                    var expr3 = new RuleRefIR(new RuleIR(leaf2, new List<RuleAlternativeIR>()));
                    labels.Add(new FieldLabelIR(label3, expr3));
                    topElements.Add(expr3);

                    classic = leaf1 + "Node";
                    break;
                }
        }

        var topExpr = topElements.Count == 1 ? topElements[0] : new SequenceIR(topElements);
        var alt = new RuleAlternativeIR(null, labels, topExpr);
        return new BackCompatBuilt
        {
            Rule = new RuleIR(ruleName, new List<RuleAlternativeIR> { alt }),
            Alt = alt,
            NodeName = nodeName,
            ClassicSubstring = classic,
            Shape = shape.ToString(),
            Tokens = tokens,
        };
    }

    private static ExpressionIR Wrap(Mod modifier, ExpressionIR inner)
    {
        return modifier switch
        {
            Mod.Optional => new OptionalIR(inner),
            Mod.Star => new ZeroOrMoreIR(inner),
            Mod.Plus => new OneOrMoreIR(inner),
            _ => new ZeroOrMoreIR(inner),
        };
    }

    // -- Random shape helpers ----------------------------------------

    private static Mod RandomMod(Random rng)
    {
        return rng.Next(3) switch
        {
            0 => Mod.Optional,
            1 => Mod.Star,
            _ => Mod.Plus,
        };
    }

    private static Mod RandomListMod(Random rng)
    {
        return rng.Next(2) == 0 ? Mod.Star : Mod.Plus;
    }

    private static VbKind RandomKind(Random rng)
    {
        return rng.Next(4) switch
        {
            0 => VbKind.LabeledToken,
            1 => VbKind.LabeledTokenAlt,
            2 => VbKind.LabeledRuleRef,
            _ => VbKind.UnlabeledRuleRef,
        };
    }

    private static VbKind RandomLabeledKind(Random rng)
    {
        return rng.Next(3) switch
        {
            0 => VbKind.LabeledToken,
            1 => VbKind.LabeledTokenAlt,
            _ => VbKind.LabeledRuleRef,
        };
    }

    private static VbKind RandomRuleRefKind(Random rng)
    {
        return rng.Next(2) == 0 ? VbKind.LabeledRuleRef : VbKind.UnlabeledRuleRef;
    }

    private static VbKind RandomTokenLabeledKind(Random rng)
    {
        return rng.Next(2) == 0 ? VbKind.LabeledToken : VbKind.LabeledTokenAlt;
    }

    private static List<VbKind> RandomKinds(Random rng, int count)
    {
        var kinds = new List<VbKind>();
        for(var i = 0; i < count; i++)
        {
            kinds.Add(RandomKind(rng));
        }

        return kinds;
    }

    // -- Emission + assertion helpers --------------------------------

    private static string EmitAst(BuiltRule built, bool emitObsoleteShims = true)
    {
        var grammar = new GrammarIR(built.Tokens, new List<RuleIR> { built.Rule });
        var context = new EmissionContext("Test.Generated", "Test", grammar, emitObsoleteShims: emitObsoleteShims);
        return AstEmitter.EmitAstNodes(context);
    }

    private static string EmitParser(BuiltRule built)
    {
        var grammar = new GrammarIR(built.Tokens, new List<RuleIR> { built.Rule });
        var context = new EmissionContext("Test.Generated", "Test", grammar);
        return ParserEmitter.Emit(context);
    }

    private static Gen<int> SeedGen()
    {
        return Gen.Choose(1, int.MaxValue - 1);
    }

    private static void AssertContains(string haystack, string needle, string context)
    {
        Assert.True(
            haystack.Contains(needle, StringComparison.Ordinal),
            $"{context}\nExpected to FIND: {needle}\nIn:\n{haystack}");
    }

    private static void AssertDoesNotContain(string haystack, string needle, string context)
    {
        Assert.False(
            haystack.Contains(needle, StringComparison.Ordinal),
            $"{context}\nExpected NOT to find: {needle}\nIn:\n{haystack}");
    }

    // Returns the balanced-brace block that begins at the first occurrence of the marker,
    // scoping an assertion to a single class, struct, or method.
    private static string ExtractBalanced(string text, string marker)
    {
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Marker not found: {marker}\nIn:\n{text}");

        var braceStart = text.IndexOf('{', start);
        Assert.True(braceStart >= 0, $"No opening brace after: {marker}");

        var depth = 0;
        var i = braceStart;
        for(; i < text.Length; i++)
        {
            if(text[i] == '{')
            {
                depth++;
            }
            else if(text[i] == '}')
            {
                depth--;
                if(depth == 0)
                {
                    i++;
                    break;
                }
            }
        }

        return text[start..i];
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while(true)
        {
            var found = haystack.IndexOf(needle, index, StringComparison.Ordinal);
            if(found < 0)
            {
                break;
            }

            count++;
            index = found + needle.Length;
        }

        return count;
    }

    private static int CountFieldDeclarations(string structBlock)
    {
        var count = 0;
        foreach(var line in structBlock.Split('\n'))
        {
            var trimmed = line.Trim();
            if(trimmed.StartsWith("public ", StringComparison.Ordinal)
                && trimmed.EndsWith(";", StringComparison.Ordinal)
                && !trimmed.Contains("struct", StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private static string PascalCase(string name)
    {
        return string.IsNullOrEmpty(name) ? name : char.ToUpperInvariant(name[0]) + name[1..];
    }

    private static string Describe(BuiltRule built)
    {
        var groupDescriptions = built.Groups.Select(group =>
        {
            var fieldDescriptions = string.Join(",", group.Fields.Select(field => field.Name + ":" + field.Type));
            return $"{group.PropertyName}[{group.Modifier}]{{{fieldDescriptions}}}";
        });
        return $"seed={built.Seed} rule={built.RuleName} leading={built.LeadingFieldName ?? "none"} groups=[{string.Join("; ", groupDescriptions)}]";
    }

    // -- Property 1: Group struct emission for anonymous groups --

    // Feature: storm-anonymous-groups, Property 1: Group struct emission for anonymous groups
    /// <summary>
    /// **Validates: Requirements 1.1, 1.2, 9.1**
    ///
    /// For any group of two or more value-bearing elements under <c>?</c>/<c>*</c>/<c>+</c>,
    /// the AST emitter output contains a <c>public struct {Rule}NodeGroup{N}</c> with
    /// exactly one field per value-bearing element (in source order) and no field for a
    /// bare structural token.
    /// </summary>
    [Property(MaxTest = Iterations)]
    public Property Property1_TwoOrMoreValueBearingGroup_ProjectsStructWithOneFieldPerElement()
    {
        var gen = SeedGen().Select(seed =>
        {
            var rng = new Random(seed);
            var includeLeading = rng.Next(2) == 0;
            var modifier = RandomMod(rng);
            var kinds = RandomKinds(rng, rng.Next(2, 5));
            var structural = rng.Next(2) == 0;
            var specs = new List<GroupSpec> { new(modifier, kinds, structural, rng.Next(2, 4)) };
            var built = BuildRule(rng, includeLeading, specs);
            built.Seed = seed;
            return built;
        });

        return Prop.ForAll(gen.ToArbitrary(), built =>
        {
            var context = Describe(built);
            var output = EmitAst(built);
            var group = built.Groups[0];

            AssertContains(output, $"public struct {group.StructName}", context);

            var structBlock = ExtractBalanced(output, $"public struct {group.StructName}");

            var previousIndex = -1;
            foreach(var field in group.Fields)
            {
                var fieldDecl = $"public {field.Type} {field.Name};";
                AssertContains(structBlock, fieldDecl, context);

                // Fields appear in source order.
                var at = structBlock.IndexOf(fieldDecl, StringComparison.Ordinal);
                Assert.True(at > previousIndex, $"{context}\nField '{fieldDecl}' is out of source order.\nStruct:\n{structBlock}");
                previousIndex = at;
            }

            // Exactly one field per value-bearing element -- structural tokens add none.
            var fieldCount = CountFieldDeclarations(structBlock);
            Assert.True(
                fieldCount == group.Fields.Count,
                $"{context}\nExpected {group.Fields.Count} struct fields, found {fieldCount}.\nStruct:\n{structBlock}");
        });
    }

    // -- Property 2: Group index uniqueness and ordering --

    // Feature: storm-anonymous-groups, Property 2: Group index uniqueness and ordering
    /// <summary>
    /// **Validates: Requirements 1.3**
    ///
    /// For any alternative containing K sibling struct-projected groups, the emitted
    /// struct names are Group0..Group{K-1} in left-to-right source order with no gaps and
    /// no duplicates.
    /// </summary>
    [Property(MaxTest = Iterations)]
    public Property Property2_MultipleGroups_ProduceUniqueSequentialIndicesInOrder()
    {
        var gen = SeedGen().Select(seed =>
        {
            var rng = new Random(seed);
            var includeLeading = rng.Next(2) == 0;
            var groupCount = rng.Next(2, 5);
            var specs = new List<GroupSpec>();
            for(var i = 0; i < groupCount; i++)
            {
                specs.Add(new GroupSpec(RandomMod(rng), RandomKinds(rng, rng.Next(2, 4)), rng.Next(2) == 0, rng.Next(2, 4)));
            }

            var built = BuildRule(rng, includeLeading, specs);
            built.Seed = seed;
            return built;
        });

        return Prop.ForAll(gen.ToArbitrary(), built =>
        {
            var context = Describe(built);
            var output = EmitAst(built);

            var previousIndex = -1;
            foreach(var group in built.Groups)
            {
                var decl = $"public struct {group.StructName}";

                var occurrences = CountOccurrences(output, decl);
                Assert.True(occurrences == 1, $"{context}\nExpected exactly one '{decl}', found {occurrences}.");

                var at = output.IndexOf(decl, StringComparison.Ordinal);
                Assert.True(at > previousIndex, $"{context}\nStruct '{decl}' is out of left-to-right order.");
                previousIndex = at;
            }

            // No gaps and no extra group beyond the highest expected index.
            AssertDoesNotContain(output, $"public struct {built.NodeName}Group{built.Groups.Count}", context);

            var classBody = ExtractBalanced(output, $"public sealed class {built.NodeName}");
            foreach(var group in built.Groups)
            {
                AssertContains(classBody, $"{group.PropertyName} {{ get; set; }}", context);
            }
        });
    }

    // -- Property 3: Parent property type matches modifier --

    // Feature: storm-anonymous-groups, Property 3: Parent property type matches modifier
    /// <summary>
    /// **Validates: Requirements 1.5**
    ///
    /// For any struct-projected group, the parent node's property type matches the group
    /// modifier: <c>?</c> -> <c>{Rule}NodeGroup{N}?</c>; <c>*</c>/<c>+</c> ->
    /// <c>ImmutableArray&lt;{Rule}NodeGroup{N}&gt;</c>. (No-modifier groups are not
    /// projected by the committed detector and so are outside this property.)
    /// </summary>
    [Property(MaxTest = Iterations)]
    public Property Property3_ParentPropertyType_MatchesGroupModifier()
    {
        var gen = SeedGen().Select(seed =>
        {
            var rng = new Random(seed);
            var includeLeading = rng.Next(2) == 0;
            var specs = new List<GroupSpec> { new(RandomMod(rng), RandomKinds(rng, rng.Next(2, 5)), rng.Next(2) == 0, rng.Next(2, 4)) };
            var built = BuildRule(rng, includeLeading, specs);
            built.Seed = seed;
            return built;
        });

        return Prop.ForAll(gen.ToArbitrary(), built =>
        {
            var context = Describe(built);
            var output = EmitAst(built);
            var group = built.Groups[0];
            var classBody = ExtractBalanced(output, $"public sealed class {built.NodeName}");

            AssertContains(classBody, $"public {group.PropertyType} {group.PropertyName} {{ get; set; }}", context);

            if(group.Modifier == Mod.Optional)
            {
                AssertContains(classBody, $"{group.StructName}? {group.PropertyName}", context);
            }
            else
            {
                AssertContains(classBody, $"ImmutableArray<{group.StructName}> {group.PropertyName}", context);
                AssertContains(classBody, $"= ImmutableArray<{group.StructName}>.Empty;", context);
            }
        });
    }

    // -- Property 4: Flat field exclusion (shims disabled) --

    // Feature: storm-anonymous-groups, Property 4: Flat field exclusion
    /// <summary>
    /// **Validates: Requirements 1.6**
    ///
    /// With obsolete shims disabled, the parent node class contains no property named
    /// after any element declared inside the group; those names appear only on the group
    /// struct. The new <c>Group{N}</c> property is present and no <c>[Obsolete]</c> shim
    /// is emitted.
    /// </summary>
    [Property(MaxTest = Iterations)]
    public Property Property4_ShimsDisabled_ParentHasNoFlatFieldForInGroupLabels()
    {
        var gen = SeedGen().Select(seed =>
        {
            var rng = new Random(seed);
            var includeLeading = rng.Next(2) == 0;
            var kinds = new List<VbKind> { RandomLabeledKind(rng) };
            kinds.AddRange(RandomKinds(rng, rng.Next(1, 4)));
            var specs = new List<GroupSpec> { new(RandomMod(rng), kinds, rng.Next(2) == 0, rng.Next(2, 4)) };
            var built = BuildRule(rng, includeLeading, specs);
            built.Seed = seed;
            return built;
        });

        return Prop.ForAll(gen.ToArbitrary(), built =>
        {
            var context = Describe(built);
            var output = EmitAst(built, emitObsoleteShims: false);
            var group = built.Groups[0];
            var classBody = ExtractBalanced(output, $"public sealed class {built.NodeName}");

            // The new group property exists; no obsolete shim is emitted.
            AssertContains(classBody, $"{group.PropertyName} {{ get; set; }}", context);
            AssertDoesNotContain(classBody, "[Obsolete", context);

            // No in-group element name leaks onto the parent as a settable property.
            foreach(var field in group.Fields)
            {
                AssertDoesNotContain(classBody, $" {field.Name} {{ get; set; }}", context);
            }
        });
    }

    // -- Property 5: Pure_Token_Alternation collapses to Token --

    // Feature: storm-anonymous-groups, Property 5: Pure_Token_Alternation collapses to Token
    /// <summary>
    /// **Validates: Requirements 2.1, 2.2**
    ///
    /// Part (a): a lone labelled token-only alternation group (one value-bearing element)
    /// is NOT struct-projected; the parent property is <c>Token?</c> for <c>?</c> and
    /// <c>ImmutableArray&lt;Token&gt;</c> for <c>*</c>/<c>+</c>, named from the label.
    /// Part (b): a labelled token-only alternation appearing inside a two-or-more
    /// value-bearing struct group becomes a <c>Token</c> struct field (no nested struct).
    /// </summary>
    [Property(MaxTest = Iterations)]
    public Property Property5_PureTokenAlternation_CollapsesToToken()
    {
        // Part (a): lone pure-token alternation group -> no struct, token promotion.
        var loneGen = SeedGen().Select(seed =>
        {
            var built = BuildLoneAlt(new Random(seed));
            built.Seed = seed;
            return built;
        });

        var loneProperty = Prop.ForAll(loneGen.ToArbitrary(), built =>
        {
            var context = $"seed={built.Seed} node={built.NodeName} lone-alt label={built.LabelName} mod={built.Modifier}";
            var output = AstEmitter.EmitAstNodes(new EmissionContext(
                "Test.Generated",
                "Test",
                new GrammarIR(built.Tokens, new List<RuleIR> { built.Rule })));

            // One value-bearing element => the group is never struct-projected.
            AssertDoesNotContain(output, "NodeGroup", context);
            AssertDoesNotContain(output, "Group0", context);

            var classBody = ExtractBalanced(output, $"public sealed class {built.NodeName}");
            if(built.Modifier == Mod.Optional)
            {
                AssertContains(classBody, $"Token? {built.LabelName} {{ get; set; }}", context);
            }
            else
            {
                AssertContains(classBody, $"ImmutableArray<Token> {built.LabelName} {{ get; set; }}", context);
            }
        });

        // Part (b): pure-token alternation field inside a 2+ struct group -> Token field.
        var structGen = SeedGen().Select(seed =>
        {
            var rng = new Random(seed);
            var includeLeading = rng.Next(2) == 0;
            var kinds = new List<VbKind> { VbKind.LabeledTokenAlt, VbKind.LabeledRuleRef };
            var specs = new List<GroupSpec> { new(RandomMod(rng), kinds, rng.Next(2) == 0, rng.Next(2, 4)) };
            var built = BuildRule(rng, includeLeading, specs);
            built.Seed = seed;
            return built;
        });

        var structProperty = Prop.ForAll(structGen.ToArbitrary(), built =>
        {
            var context = Describe(built);
            var output = EmitAst(built);
            var group = built.Groups[0];
            var altField = group.Fields[0];

            AssertContains(output, $"public struct {group.StructName}", context);

            var structBlock = ExtractBalanced(output, $"public struct {group.StructName}");

            // The token-only alternation collapses to a single Token field, not a nested struct.
            AssertContains(structBlock, $"public Token {altField.Name};", context);
            AssertDoesNotContain(output, $"public struct {group.StructName}Group0", context);
        });

        return loneProperty.And(structProperty);
    }

    // -- Property 6: Parser struct construction per iteration --

    // Feature: storm-anonymous-groups, Property 6: Parser struct construction per iteration
    /// <summary>
    /// **Validates: Requirements 3.1, 3.5, 9.2**
    ///
    /// For any <c>*</c>/<c>+</c> struct-projected group, the parser method builds a
    /// <c>{Rule}NodeGroup{N}</c> per iteration -- a <c>CreateBuilder&lt;...&gt;()</c>
    /// accumulator plus a <c>new {Rule}NodeGroup{N} { ... }</c> construction -- assigns the
    /// finalised list to <c>node.Group{N}</c>, and never writes an in-group field as a
    /// scalar node property.
    /// </summary>
    [Property(MaxTest = Iterations)]
    public Property Property6_StarPlusGroup_ParserBuildsStructPerIteration()
    {
        var gen = SeedGen().Select(seed =>
        {
            var rng = new Random(seed);
            var includeLeading = rng.Next(2) == 0;
            var specs = new List<GroupSpec> { new(RandomListMod(rng), RandomKinds(rng, rng.Next(2, 5)), rng.Next(2) == 0, rng.Next(2, 4)) };
            var built = BuildRule(rng, includeLeading, specs);
            built.Seed = seed;
            return built;
        });

        return Prop.ForAll(gen.ToArbitrary(), built =>
        {
            var context = Describe(built);
            var parser = EmitParser(built);
            var method = ExtractBalanced(parser, $"private static {built.NodeName}? _Parse{built.RuleName}");
            var group = built.Groups[0];

            AssertContains(method, $"ImmutableArray.CreateBuilder<{group.StructName}>()", context);
            AssertContains(method, $"new {group.StructName} {{", context);
            AssertContains(method, $"node.{group.PropertyName} = ", context);

            // No last-wins scalar assignment for any in-group field.
            foreach(var field in group.Fields)
            {
                AssertDoesNotContain(method, $"node.{field.Name} ", context);
            }
        });
    }

    // -- Property 7: Backward compatibility for non-group rules --

    // Feature: storm-anonymous-groups, Property 7: Backward compatibility for non-group rules
    /// <summary>
    /// **Validates: Requirements 6.1, 6.2**
    ///
    /// For any rule whose anonymous groups all hold fewer than two value-bearing elements
    /// (or has no groups at all), the detector reports no groups, neither emitter produces
    /// a <c>NodeGroup</c> struct or <c>Group{N}</c> property (the only artifacts this
    /// feature adds), and the classic promotion (<c>ImmutableArray&lt;T&gt;</c> /
    /// <c>T?</c> / <c>T</c>) is retained.
    /// </summary>
    [Property(MaxTest = Iterations)]
    public Property Property7_SubTwoValueBearingRules_ProduceNoGroupProjection()
    {
        var gen = SeedGen().Select(seed =>
        {
            var built = BuildBackCompat(new Random(seed));
            built.Seed = seed;
            return built;
        });

        return Prop.ForAll(gen.ToArbitrary(), built =>
        {
            var context = $"seed={built.Seed} node={built.NodeName} shape={built.Shape} classic={built.ClassicSubstring}";

            // The detection gate returns nothing for a sub-two-value-bearing shape.
            var detected = AstEmitter.DetectAnonymousGroups(built.Alt);
            Assert.True(detected.Count == 0, $"{context}\nExpected no detected groups, found {detected.Count}.");

            var grammar = new GrammarIR(built.Tokens, new List<RuleIR> { built.Rule });
            var astContext = new EmissionContext("Test.Generated", "Test", grammar);
            var ast = AstEmitter.EmitAstNodes(astContext);
            var parser = ParserEmitter.Emit(astContext);

            // No group-projection artifacts in either emitter.
            AssertDoesNotContain(ast, "NodeGroup", context);
            AssertDoesNotContain(ast, "Group0", context);
            AssertDoesNotContain(parser, "NodeGroup", context);

            // The classic promotion is retained.
            AssertContains(ast, built.ClassicSubstring, context);
        });
    }

    // -- Property 8: Shim property forwarding correctness --

    // Feature: storm-anonymous-groups, Property 8: Shim property forwarding correctness
    /// <summary>
    /// **Validates: Requirements 5.1, 5.2**
    ///
    /// For a <c>*</c>/<c>+</c> struct group with shims enabled, each scalar shim forwards
    /// through <c>Group{N}[^1].{Field}</c>, and each rule-reference field additionally
    /// gets a list shim forwarding through <c>Group{N}.Select(g =&gt; g.{Field})</c>. The
    /// <c>[Obsolete("Use Group{N}")]</c> attribute is present.
    /// </summary>
    [Property(MaxTest = Iterations)]
    public Property Property8_StarPlusGroup_ShimsForwardToGroupData()
    {
        var gen = SeedGen().Select(seed =>
        {
            var rng = new Random(seed);
            var includeLeading = rng.Next(2) == 0;
            var kinds = new List<VbKind> { RandomTokenLabeledKind(rng), VbKind.LabeledRuleRef };
            if(rng.Next(2) == 0)
            {
                kinds.Add(VbKind.UnlabeledRuleRef);
            }

            var specs = new List<GroupSpec> { new(RandomListMod(rng), kinds, rng.Next(2) == 0, rng.Next(2, 4)) };
            var built = BuildRule(rng, includeLeading, specs);
            built.Seed = seed;
            return built;
        });

        return Prop.ForAll(gen.ToArbitrary(), built =>
        {
            var context = Describe(built);
            var output = EmitAst(built, emitObsoleteShims: true);
            var group = built.Groups[0];
            var classBody = ExtractBalanced(output, $"public sealed class {built.NodeName}");

            AssertContains(classBody, $"[Obsolete(\"Use {group.PropertyName}\")]", context);

            foreach(var field in group.Fields)
            {
                if(field.IsToken)
                {
                    // Scalar shim for a token field: last-wins forward.
                    AssertContains(classBody, $"{group.PropertyName}[^1].{field.Name}", context);
                }
                else if(field.IsNode && field.IsLabeled)
                {
                    // Labelled rule reference: scalar (last-wins) shim + list projection shim.
                    AssertContains(classBody, $"{group.PropertyName}[^1].{field.Name}", context);
                    AssertContains(classBody, $"{group.PropertyName}.Select(g => g.{field.Name})", context);
                }
                else if(field.IsNode)
                {
                    // Unlabelled rule reference: list projection shim only.
                    AssertContains(classBody, $"{group.PropertyName}.Select(g => g.{field.Name})", context);
                }
            }
        });
    }

    // -- Property 9: Children() yields all node-typed group fields --

    // Feature: storm-anonymous-groups, Property 9: Children() yields all node-typed group fields
    /// <summary>
    /// **Validates: Requirements 7.2**
    ///
    /// For any <c>*</c>/<c>+</c> struct group with node-typed fields, the emitted
    /// <c>Children()</c> override iterates the group list with a <c>foreach</c> and yields
    /// each node-typed field; token fields are not yielded.
    /// </summary>
    [Property(MaxTest = Iterations)]
    public Property Property9_StarPlusGroup_ChildrenYieldsNodeFields()
    {
        var gen = SeedGen().Select(seed =>
        {
            var rng = new Random(seed);
            var includeLeading = rng.Next(2) == 0;
            var kinds = new List<VbKind> { RandomRuleRefKind(rng) };
            kinds.AddRange(RandomKinds(rng, rng.Next(1, 4)));
            var specs = new List<GroupSpec> { new(RandomListMod(rng), kinds, rng.Next(2) == 0, rng.Next(2, 4)) };
            var built = BuildRule(rng, includeLeading, specs);
            built.Seed = seed;
            return built;
        });

        return Prop.ForAll(gen.ToArbitrary(), built =>
        {
            var context = Describe(built);
            var output = EmitAst(built);
            var group = built.Groups[0];
            var classBody = ExtractBalanced(output, $"public sealed class {built.NodeName}");
            var children = ExtractBalanced(classBody, "public override IEnumerable<SyntaxNode> Children()");

            AssertContains(children, $"foreach (var groupItem in {group.PropertyName})", context);

            foreach(var field in group.Fields)
            {
                if(field.IsNode)
                {
                    AssertContains(children, $"yield return groupItem.{field.Name};", context);
                }
                else
                {
                    AssertDoesNotContain(children, $"yield return groupItem.{field.Name};", context);
                }
            }
        });
    }
}