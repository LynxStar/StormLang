using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;
using Storm.SourceGenerator.Validate;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Left recursion hidden behind a rule that can succeed without consuming a token. The generated parser
/// memoizes a rule only after its body returns, so a call that re-enters the same rule at the same position
/// never ends; <see cref="LeftRecursionPass"/> must refuse the grammar before any parser is emitted. Every
/// grammar is built by hand: tokens <c>A</c>, <c>B</c> and <c>C</c>, and whatever rules each test declares.
/// </summary>
[Trait("Tier", "2")]
public sealed class NullableLeftRecursionTests
{
    private static readonly StormExpression A = Ref("A");
    private static readonly StormExpression B = Ref("B");
    private static readonly StormExpression C = Ref("C");
    private static readonly StormExpression R = Ref("R");
    private static readonly StormExpression Empty = Ref("Empty");

    // ---------------------------------------------------------------
    //  Refused: a rule re-entered before anything is consumed
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecursionBehindANullableRuleIsRefusedWhicheverIsDeclaredFirst(bool nullableRuleFirst)
    {
        // On `b`, Empty succeeds consuming nothing and R is called again where it started.
        var grammar = new Grammar();
        if(nullableRuleFirst)
        {
            _ = grammar.Rule("Empty", Opt(A));
        }

        _ = grammar.Rule("R", Alt(Seq(Empty, R), B));
        if(!nullableRuleFirst)
        {
            _ = grammar.Rule("Empty", Opt(A));
        }

        AssertRefused(grammar, "R -> R");
    }

    [Fact]
    public void RecursionBehindSeveralNullableRulesIsRefused()
    {
        // Chain is empty only because both of the rules it calls are, one of them declared after it.
        var grammar = new Grammar()
            .Rule("R", Alt(Seq(Ref("First"), Ref("Chain"), R), B))
            .Rule("First", Opt(A))
            .Rule("Chain", Seq(Ref("First"), Ref("Last")))
            .Rule("Last", Star(Group(Seq(B, C))));

        AssertRefused(grammar, "R -> R");
    }

    [Fact]
    public void MutualRecursionBehindANullableRuleIsRefused()
    {
        var grammar = new Grammar()
            .Rule("P", Alt(Seq(Empty, Ref("Q")), A))
            .Rule("Q", Alt(Seq(Empty, Ref("P"), C), B))
            .Rule("Empty", Opt(A));

        AssertRefused(grammar, "P -> Q -> P");
    }

    [Fact]
    public void RulesWhoseEmptinessDependsOnEachOtherAreRefusedAndTheAnalysisEnds()
    {
        // N1 is empty through its optional; N2 only through N1; each calls the other first.
        var grammar = new Grammar()
            .Rule("R", Alt(Seq(Ref("N1"), R), B))
            .Rule("N1", Alt(Ref("N2"), Opt(A)))
            .Rule("N2", Seq(Ref("N1"), Opt(C)));

        AssertRefused(grammar, "N1 -> N2 -> N1", "R -> R");
    }

    public static TheoryData<string, StormExpression> RecursionInsideAnOperand => new()
    {
        { "optional", Alt(Seq(Opt(Group(Seq(Empty, R))), A), B) },
        { "zero or more", Alt(Seq(Star(Group(Seq(Empty, R))), A), B) },
        { "one or more", Alt(Plus(Group(Seq(Empty, R))), B) },
        { "positive predicate", Alt(Seq(And(Group(Seq(Empty, R))), A), B) },
        { "negative predicate after a nullable rule", Alt(Seq(Empty, Not(R), A), B) },
        { "nullable rule after a predicate", Alt(Seq(And(A), Empty, R), B) },
        { "labels", Alt(Seq(Label("empty", Empty), Label("again", R)), B) },
        { "grouped nullable rule", Alt(Seq(Group(Empty), R), B) },
        { "named alternatives", Alt(Named(Seq(Empty, R), "Loop"), Named(B, "Leaf")) },
        { "choice with a nullable arm", Alt(Seq(Group(Alt(A, Empty)), R), B) },
        { "an empty alternative that can fail, before the recursive one", Alt(Not(B), Seq(Empty, R), B) },
    };

    [Theory]
    [MemberData(nameof(RecursionInsideAnOperand))]
    public void RecursionThroughAnOperandWrapperOrLabelIsRefused(string shape, StormExpression body)
    {
        _ = shape;
        var grammar = new Grammar()
            .Rule("Empty", Opt(A))
            .Rule("R", body);

        AssertRefused(grammar, "R -> R");
    }

    // ---------------------------------------------------------------
    //  Refused: qualified references land on the declaration the grammar lookup returns
    // ---------------------------------------------------------------

    [Fact]
    public void AQualifiedNullableRuleHidesRecursionLikeAnUnqualifiedOne()
    {
        var grammar = new Grammar()
            .Rule("R", Alt(Seq(Qualified("Ns", "Empty"), R), B))
            .Namespaced("Ns", "Empty", Opt(A));

        AssertRefused(grammar, "R -> R");
    }

    [Fact]
    public void ACycleThroughANamespacedRuleIsRefusedAndNamedAsItIsReferenced()
    {
        var grammar = new Grammar()
            .Rule("R", Alt(Qualified("Ns", "Wrap"), B))
            .Rule("Empty", Opt(A))
            .Namespaced("Ns", "Wrap", Seq(Empty, R));

        AssertRefused(grammar, "R -> Ns.Wrap -> R");
    }

    [Fact]
    public void ADeclarationAlsoImportedUnderAnAliasIsNamedPlainly()
    {
        // A file imported both plainly and under an alias contributes the same declaration twice.
        var grammar = new Grammar()
            .Rule("R", Alt(Qualified("Ns", "Wrap"), B))
            .Rule("Wrap", Seq(Opt(A), R))
            .AlsoNamespaced("Ns", "Wrap");

        AssertRefused(grammar, "R -> Wrap -> R");
    }

    // ---------------------------------------------------------------
    //  Refused: inline blocks, walked as the emitter writes them
    // ---------------------------------------------------------------

    [Fact]
    public void RecursionInAnInlinePrefixBehindANullableRuleIsRefused()
    {
        var grammar = new Grammar()
            .Rule("R", Alt(Inline("Head", Arm("X")), B))
            .Rule("Head", Seq(Empty, R))
            .Rule("X", Seq(Ref("Head"), C))
            .Rule("Empty", Opt(A));

        AssertRefused(grammar, "R -> R");
    }

    [Fact]
    public void AnArmTailAfterANullablePrefixIsRefused()
    {
        var grammar = new Grammar()
            .Rule("R", Alt(Inline("Head", Arm("X", label: "x")), B))
            .Rule("Head", Opt(A))
            .Rule("X", Seq(Label("head", Ref("Head")), R));

        AssertRefused(grammar, "R -> R");
    }

    [Fact]
    public void AnArmThatSpellsOutItsPrefixHasTheSameTail()
    {
        var grammar = new Grammar()
            .Rule("R", Alt(Inline("Head", Arm("X")), B))
            .Rule("Head", Seq(Opt(A), Star(B)))
            .Rule("X", Seq(Label("a", Opt(A)), Star(B), R));

        AssertRefused(grammar, "R -> R");
    }

    [Fact]
    public void ANestedInlineBlockBehindNullablePrefixesIsRefused()
    {
        var grammar = new Grammar()
            .Rule("R", Alt(Inline("Outer", NestedArm(Inline("Inner", Arm("Y")))), B))
            .Rule("Outer", Opt(A))
            .Rule("Inner", Star(C))
            .Rule("Y", Seq(Ref("Inner"), R));

        AssertRefused(grammar, "R -> R");
    }

    [Fact]
    public void TheCyclePathNamesOnlyRulesTheInlineBlockCalls()
    {
        // The emitter parses Head's body and X's tail in place; it never calls Head or X.
        var grammar = new Grammar()
            .Rule("R", Alt(Inline("Head", Arm("X")), B))
            .Rule("Head", Empty)
            .Rule("X", Seq(Ref("Head"), Ref("Mid")))
            .Rule("Mid", Seq(R, C))
            .Rule("Empty", Opt(A));

        AssertRefused(grammar, "R -> Mid -> R");
    }

    // ---------------------------------------------------------------
    //  Accepted: every recursive call follows a consumed token
    // ---------------------------------------------------------------

    public static TheoryData<string, StormExpression> RecursionAfterConsumption => new()
    {
        { "right recursion", Alt(Seq(A, R), B) },
        { "a token after the nullable rule", Alt(Seq(Empty, A, R), B) },
        { "one or more of a nullable rule", Alt(Seq(Plus(Empty), R), B) },
        { "one or more of an all-optional group", Alt(Seq(Plus(Group(Seq(Empty, Opt(C)))), R), B) },
        { "a token with a mode action and an empty-matching pattern", Alt(Seq(Ref("Open"), R), B) },
        { "an acyclic nullable chain before a token", Alt(Seq(Empty, Ref("Chain"), A, R), B) },
    };

    [Theory]
    [MemberData(nameof(RecursionAfterConsumption))]
    public void RecursionAfterAConsumedTokenIsAccepted(string shape, StormExpression body)
    {
        _ = shape;
        var grammar = new Grammar()
            .Token(new StormTokenDecl("Open", new StormRegexLiteralExpr("x*"), 0, 90, 1, new StormPushAction("Inner")))
            .Rule("Empty", Opt(A))
            .Rule("Chain", Seq(Empty, Star(B)))
            .Rule("R", body);

        Assert.Empty(grammar.Check());
    }

    [Fact]
    public void AnArmTailAfterAConsumingPrefixIsAccepted()
    {
        var grammar = new Grammar()
            .Rule("R", Alt(Inline("Head", Arm("X")), B))
            .Rule("Head", Seq(Opt(C), A))
            .Rule("X", Seq(Ref("Head"), R));

        Assert.Empty(grammar.Check());
    }

    // ---------------------------------------------------------------
    //  Left to InlinePrefixVerification, which runs after this pass
    // ---------------------------------------------------------------

    // Stripped as lowering strips an arm, each of these would make R call itself first.
    public static TheoryData<string, StormExpression, StormExpression> MalformedInlineBlocks => new()
    {
        { "an arm that does not begin with the prefix", Opt(A), Seq(Opt(C), R) },
        { "a prefix with an alternation body", Alt(Opt(A), C), Seq(Ref("Head"), R) },
        { "an arm with an alternation body after an empty prefix", Seq(), Alt(Seq(Ref("Head"), R), C) },
    };

    [Theory]
    [MemberData(nameof(MalformedInlineBlocks))]
    public void AnInlineBlockTheVerifierRejectsContributesNoCall(string shape, StormExpression head, StormExpression x)
    {
        _ = shape;
        var grammar = new Grammar()
            .Rule("R", Alt(Inline("Head", Arm("X")), B))
            .Rule("Head", head)
            .Rule("X", x);

        Assert.Empty(grammar.Check());
    }

    [Fact]
    public void APrefixOrArmThatIsNotARuleContributesNoCall()
    {
        var grammar = new Grammar()
            .Rule("R", Alt(Inline("Missing", Arm("X")), Inline("Head", Arm("Absent")), Inline("A", Arm("Y")), B))
            .Rule("Head", Opt(A))
            .Rule("X", Seq(Ref("Missing"), R))
            .Rule("Y", Seq(A, R));

        Assert.Empty(grammar.Check());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnInlineBlockThatContainsItselfEnds(bool throughThePrefix)
    {
        // Either Head's body is a block with Head as its prefix, or X's tail holds the block that names X.
        var grammar = throughThePrefix
            ? new Grammar()
                .Rule("R", Alt(Inline("Head", Arm("X")), B))
                .Rule("Head", Inline("Head", Arm("X")))
                .Rule("X", Seq(Ref("Head"), R))
            : new Grammar()
                .Rule("R", Alt(Inline("Head", Arm("X")), B))
                .Rule("Head", Opt(A))
                .Rule("X", Seq(Ref("Head"), Inline("Head", Arm("X")), R));

        var exception = Record.Exception(grammar.Check);

        Assert.Null(exception);
    }

    // ---------------------------------------------------------------
    //  Helpers
    // ---------------------------------------------------------------

    private static void AssertRefused(Grammar grammar, params string[] cycles)
    {
        var diagnostics = grammar.Check();

        var expected = cycles
            .Select(cycle => ($"Left-recursion detected: {cycle}", grammar.LineOf(cycle.Split(' ')[0])))
            .OrderBy(pair => pair.Item1, StringComparer.Ordinal)
            .ToList();
        var actual = diagnostics
            .Select(diagnostic => (diagnostic.Message, diagnostic.Line))
            .OrderBy(pair => pair.Message, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expected, actual);
        Assert.All(diagnostics, diagnostic =>
        {
            Assert.Equal(StormDiagnosticIds.STORM_LEFT_RECURSION, diagnostic.Id);
            Assert.Equal(StormDiagnosticSeverity.Error, diagnostic.Severity);
        });
    }

    private static StormExpression Ref(string name)
    {
        return new StormReferenceExpr(name);
    }

    private static StormExpression Qualified(string qualifier, string name)
    {
        return new StormQualifiedReferenceExpr(qualifier, name);
    }

    private static StormExpression Seq(params StormExpression[] elements)
    {
        return new StormSequenceExpr(elements);
    }

    private static StormExpression Alt(params StormExpression[] alternatives)
    {
        return new StormAlternationExpr(alternatives);
    }

    private static StormExpression Opt(StormExpression inner)
    {
        return new StormOptionalExpr(inner);
    }

    private static StormExpression Star(StormExpression inner)
    {
        return new StormZeroOrMoreExpr(inner);
    }

    private static StormExpression Plus(StormExpression inner)
    {
        return new StormOneOrMoreExpr(inner);
    }

    private static StormExpression Group(StormExpression inner)
    {
        return new StormGroupExpr(inner);
    }

    private static StormExpression And(StormExpression inner)
    {
        return new StormPredicateExpr(inner, IsNegated: false);
    }

    private static StormExpression Not(StormExpression inner)
    {
        return new StormPredicateExpr(inner, IsNegated: true);
    }

    private static StormExpression Label(string label, StormExpression element)
    {
        return new StormLabeledExpr(label, element);
    }

    private static StormExpression Named(StormExpression expression, string name)
    {
        return new StormNamedAlternative(expression, name);
    }

    private static StormExpression Inline(string prefix, params StormInlineArm[] arms)
    {
        return new StormInlineBlockExpr(new StormReferenceExpr(prefix), arms, 0, 0, 0);
    }

    private static StormInlineArm Arm(string rule, string? label = null)
    {
        return new StormInlineArm(new StormReferenceExpr(rule), rule + "Arm", label);
    }

    private static StormInlineArm NestedArm(StormExpression block)
    {
        return new StormInlineArm(block, "Nested", null);
    }

    /// <summary>
    /// The three inputs the pass reads from a <c>StormGrammar</c>, kept the way the grammar keeps them: every
    /// declaration in order, namespaced ones among them, and a lookup that tries plain names before qualified
    /// ones. Each declaration sits on its own line so a diagnostic's line names the rule it points at.
    /// </summary>
    private sealed class Grammar
    {
        private readonly List<StormDeclaration> declarations = [];
        private readonly Dictionary<string, StormDeclaration> plain = [];
        private readonly Dictionary<string, StormDeclaration> namespaced = [];

        public Grammar()
        {
            foreach(var name in new[] { "A", "B", "C" })
            {
                _ = Token(new StormTokenDecl(name, new StormStringLiteralExpr(name.ToLowerInvariant()), 0, NextLine, 1));
            }
        }

        private int NextLine => declarations.Count + 1;

        public Grammar Token(StormTokenDecl token)
        {
            declarations.Add(token);
            plain[token.Name] = token;
            return this;
        }

        public Grammar Rule(string name, StormExpression body)
        {
            var rule = new StormRuleDecl(name, body, 0, NextLine, 1);
            declarations.Add(rule);
            plain[name] = rule;
            return this;
        }

        public Grammar Namespaced(string qualifier, string name, StormExpression body)
        {
            var rule = new StormRuleDecl(name, body, 0, NextLine, 1);
            declarations.Add(rule);
            namespaced[qualifier + "." + name] = rule;
            return this;
        }

        public Grammar AlsoNamespaced(string qualifier, string name)
        {
            var rule = plain[name];
            declarations.Add(rule);
            namespaced[qualifier + "." + name] = rule;
            return this;
        }

        public int LineOf(string name)
        {
            return Lookup(name)!.Line;
        }

        public List<StormDiagnostic> Check()
        {
            return LeftRecursionPass.Run(declarations, namespaced, Lookup);
        }

        private StormDeclaration? Lookup(string name)
        {
            return plain.TryGetValue(name, out var declaration) || namespaced.TryGetValue(name, out declaration)
                ? declaration
                : null;
        }
    }
}