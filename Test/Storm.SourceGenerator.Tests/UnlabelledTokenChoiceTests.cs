// An unlabelled alternation such as `name:IDENT (SEMI | COMMA)` fills no field: the parser matches one of its tokens
// and moves on. It can do that only when every alternative is a single token, so lowering refuses any other shape
// instead of emitting a parser that passes over it.

using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Tests;

public sealed class UnlabelledTokenChoiceTests
{
    private static readonly TokenIR Ident = new("IDENT", new StringLiteralIR("x"), isHidden: false, declarationIndex: 0);
    private static readonly TokenIR Semi = new("SEMI", new StringLiteralIR(";"), isHidden: false, declarationIndex: 1);
    private static readonly TokenIR Comma = new("COMMA", new StringLiteralIR(","), isHidden: false, declarationIndex: 2);
    private static readonly RuleIR Args = new("Args", [new RuleAlternativeIR(null, [], new TokenRefIR(Ident))]);

    [Theory]
    [Trait("Tier", "1")]
    [InlineData("tokens", true)]
    [InlineData("literals", true)]
    [InlineData("rule", false)]
    [InlineData("sequence", false)]
    [InlineData("nested choice", false)]
    public void OnlyAChoiceBetweenSingleTokensIsATokenChoice(string arms, bool isTokenChoice)
    {
        Assert.Equal(isTokenChoice, ChoiceOf(arms).IsTokenChoice);
    }

    [Theory]
    [Trait("Tier", "1")]
    [InlineData("rule")]
    [InlineData("sequence")]
    [InlineData("nested choice")]
    public void AnUnlabelledChoiceOfAnyOtherShapeIsRefusedNamingItsRule(string arms)
    {
        var refusal = Assert.Single(Refusals(Sequence(new TokenRefIR(Ident), ChoiceOf(arms))));

        Assert.Contains("unlabelled alternation in rule 'Item'", refusal);
    }

    [Theory]
    [Trait("Tier", "1")]
    [InlineData("?")]
    [InlineData("*")]
    [InlineData("+")]
    public void TheRefusalReachesAChoiceInsideAnOptionalOrARepetition(string modifier)
    {
        var modified = Modified(modifier, Sequence(new TokenRefIR(Comma), ChoiceOf("rule")));

        _ = Assert.Single(Refusals(Sequence(new TokenRefIR(Ident), modified)));
    }

    [Fact]
    [Trait("Tier", "1")]
    public void AChoiceBetweenSingleTokensIsNotRefused()
    {
        Assert.Empty(Refusals(Sequence(new TokenRefIR(Ident), ChoiceOf("tokens"))));
    }

    [Fact]
    [Trait("Tier", "1")]
    public void AChoiceInsideAPredicateIsLeftToThePredicate()
    {
        // &(COMMA SEMI | SEMI COMMA) is a valid predicate: a fixed-width lookahead, not a token to consume.
        var lookahead = new AlternationIR(
        [
            Sequence(new TokenRefIR(Comma), new TokenRefIR(Semi)),
            Sequence(new TokenRefIR(Semi), new TokenRefIR(Comma)),
        ]);

        Assert.Empty(Refusals(Sequence(new TokenRefIR(Ident), new PredicateIR(lookahead, isNegated: false))));
    }

    [Theory]
    [Trait("Tier", "2")]
    [InlineData("sequence", "ok")]
    [InlineData("optional", @"_ok\d+")]
    [InlineData("zero or more", @"_ok\d+")]
    [InlineData("one or more", @"_ok\d+")]
    [InlineData("group", "_g0Ok")]
    public void AnUnlabelledTokenChoiceMatchesOneOfItsTokensAgainstTheEnclosingFlag(string position, string flag)
    {
        var parser = ParserEmitter.Emit(ContextOf(ItemWithChoiceIn(position)));

        Assert.Matches($@"if \(({flag}) && \(Match\(\(int\)TokenKind\.SEMI\) \?\? Match\(\(int\)TokenKind\.COMMA\)\) == null\) \1 = false;", parser);
    }

    [Fact]
    [Trait("Tier", "3")]
    public void AGrammarHoldingAnUnlabelledChoiceOfOtherShapesDoesNotLower()
    {
        var result = GrammarIRLowering.Lower(ValidationTestHelpers.BuildGrammar(
            "token IDENT = [a-z]+;\ntoken SEMI = \";\";\ntoken LT = \"<\";\ntoken GT = \">\";\n" +
            "rule Args = LT IDENT GT;\nrule Item = name:IDENT (SEMI | Args);\n"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Id == StormDiagnosticIds.STORM_UNSUPPORTED_CONSTRUCT && d.Message.Contains("rule 'Item'"));
    }

    private static IReadOnlyList<string> Refusals(ExpressionIR expression)
    {
        return UnemittableShapes.Find("Item", new RuleAlternativeIR(null, [], expression));
    }

    private static AlternationIR ChoiceOf(string arms)
    {
        return arms switch
        {
            "tokens" => new AlternationIR([new TokenRefIR(Semi), new TokenRefIR(Comma)]),
            "literals" => new AlternationIR([new StringLiteralIR(";"), new StringLiteralIR(",")]),
            "rule" => new AlternationIR([new TokenRefIR(Semi), new RuleRefIR(Args)]),
            "sequence" => new AlternationIR([new TokenRefIR(Semi), Sequence(new TokenRefIR(Comma), new TokenRefIR(Semi))]),
            "nested choice" => new AlternationIR([new TokenRefIR(Semi), new AlternationIR([new TokenRefIR(Comma), new TokenRefIR(Semi)])]),
            _ => throw new ArgumentOutOfRangeException(nameof(arms), arms, null),
        };
    }

    private static ExpressionIR Modified(string modifier, ExpressionIR inner)
    {
        return modifier switch
        {
            "?" => new OptionalIR(inner),
            "*" => new ZeroOrMoreIR(inner),
            "+" => new OneOrMoreIR(inner),
            _ => throw new ArgumentOutOfRangeException(nameof(modifier), modifier, null),
        };
    }

    private static RuleAlternativeIR ItemWithChoiceIn(string position)
    {
        var head = new TokenRefIR(Ident);
        var choice = ChoiceOf("tokens");
        if(position == "group")
        {
            var key = new TokenRefIR(Ident);
            var value = new TokenRefIR(Ident);

            return new RuleAlternativeIR(
                null,
                [new FieldLabelIR("head", head), new FieldLabelIR("key", key), new FieldLabelIR("value", value)],
                Sequence(head, new ZeroOrMoreIR(Sequence(key, choice, value))));
        }

        var placed = position switch
        {
            "sequence" => choice,
            "optional" => Modified("?", choice),
            "zero or more" => Modified("*", choice),
            "one or more" => Modified("+", choice),
            _ => throw new ArgumentOutOfRangeException(nameof(position), position, null),
        };

        return new RuleAlternativeIR(null, [new FieldLabelIR("name", head)], Sequence(head, placed));
    }

    private static EmissionContext ContextOf(RuleAlternativeIR item)
    {
        var grammar = new GrammarIR([Ident, Semi, Comma], [Args, new RuleIR("Item", [item])]);

        return new EmissionContext("Test.Generated", "Test", grammar, emitObsoleteShims: false);
    }

    private static SequenceIR Sequence(params ExpressionIR[] elements)
    {
        return new SequenceIR(elements);
    }
}