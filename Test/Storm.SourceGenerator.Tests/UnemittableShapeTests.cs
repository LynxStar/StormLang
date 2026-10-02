// Lowering refuses a shape only where a generated node has no property for what the parser would read, or the parser
// could never match it. Every other shape is emitted: an optional, repetition or choice nests at any depth.

using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Tests;

public sealed class UnemittableShapeTests
{
    private static readonly TokenIR Ident = new("IDENT", new StringLiteralIR("x"), isHidden: false, declarationIndex: 0);
    private static readonly TokenIR Semi = new("SEMI", new StringLiteralIR(";"), isHidden: false, declarationIndex: 1);
    private static readonly TokenIR Comma = new("COMMA", new StringLiteralIR(","), isHidden: false, declarationIndex: 2);
    private static readonly TokenIR Lp = new("LP", new StringLiteralIR("("), isHidden: false, declarationIndex: 3);
    private static readonly TokenIR Rp = new("RP", new StringLiteralIR(")"), isHidden: false, declarationIndex: 4);
    private static readonly TokenIR Space = new("SP", new StringLiteralIR(" "), isHidden: true, declarationIndex: 5);
    private static readonly RuleIR Args = new("Args", [new RuleAlternativeIR(null, [], new TokenRefIR(Ident))]);

    [Theory]
    [Trait("Tier", "1")]
    [InlineData("held:(SEMI | Args)", "The labelled alternation 'held' in rule 'Item' must be a choice between single tokens")]
    [InlineData("(key:IDENT held:(SEMI | Args))*", "The labelled alternation 'held' in rule 'Item' must be a choice between single tokens")]
    [InlineData("held:(COMMA SEMI)", "The label 'held' in rule 'Item' is on a group with no ?, * or +")]
    [InlineData("(key:IDENT (COMMA Args)? value:IDENT)*", "Rule 'Item' parses an unlabelled 'Args' inside a repeated or optional group, where no field of the group keeps its node")]
    [InlineData("SP", "Rule 'Item' names the hidden token 'SP', which the parser never sees")]
    public void AShapeNoPropertyCanHoldOrNoParserCanMatchIsRefused(string shape, string refusal)
    {
        Assert.StartsWith(refusal, Assert.Single(UnemittableShapes.Find("Item", Item(shape))));
    }

    [Theory]
    [Trait("Tier", "1")]
    [InlineData("held:(COMMA (SEMI | LP) RP)?")]
    [InlineData("first:Args (LP (COMMA rest:Args)* RP)?")]
    [InlineData("(key:IDENT COMMA? value:IDENT)*")]
    [InlineData("first:Args (COMMA (LP Args RP))*")]
    [InlineData("Args?")]
    public void AnOptionalRepetitionOrChoiceNestedAtAnyDepthIsNotRefused(string shape)
    {
        Assert.Empty(UnemittableShapes.Find("Item", Item(shape)));
    }

    [Fact]
    [Trait("Tier", "3")]
    public void AnUnlabelledGroupWrittenInsideASequenceJoinsIt_SoARepeatedReferenceInItIsDirect()
    {
        var lowering = GrammarIRLowering.Lower(ValidationTestHelpers.BuildGrammar(
            "token IDENT = [a-z]+;\ntoken COMMA = \",\";\ntoken LP = \"(\";\ntoken RP = \")\";\n" +
            "rule Elem = IDENT;\nrule Parens = first:Elem (COMMA (LP Elem RP))*;\n"));

        Assert.True(lowering.Succeeded, string.Join("\n", lowering.Diagnostics.Select(d => d.Message)));

        var parens = (SequenceIR)lowering.Grammar!.Rules.Single(rule => rule.Name == "Parens").Alternatives.Single().Expression;
        var repeated = Assert.IsType<SequenceIR>(Assert.IsType<ZeroOrMoreIR>(parens.Elements[1]).Inner);

        Assert.Equal(new[] { "TokenRefIR", "TokenRefIR", "RuleRefIR", "TokenRefIR" }, repeated.Elements.Select(element => element.GetType().Name));
    }

    // Builds each shape directly, as lowering would: a label names the element written after it.
    private static RuleAlternativeIR Item(string shape)
    {
        var labels = new List<FieldLabelIR>();

        T labelled<T>(string label, T expr)
            where T : ExpressionIR
        {
            labels.Add(new FieldLabelIR(label, expr));
            return expr;
        }

        TokenRefIR token(TokenIR kind)
        {
            return new TokenRefIR(kind);
        }

        RuleRefIR reference()
        {
            return new RuleRefIR(Args);
        }

        AlternationIR mixedChoice()
        {
            return new AlternationIR([token(Semi), reference()]);
        }

        ExpressionIR body = shape switch
        {
            "held:(SEMI | Args)" => labelled("held", mixedChoice()),
            "(key:IDENT held:(SEMI | Args))*" => new ZeroOrMoreIR(new SequenceIR([labelled("key", token(Ident)), labelled("held", mixedChoice())])),
            "held:(COMMA SEMI)" => labelled("held", new SequenceIR([token(Comma), token(Semi)])),
            "(key:IDENT (COMMA Args)? value:IDENT)*" => new ZeroOrMoreIR(new SequenceIR(
                [labelled("key", token(Ident)), new OptionalIR(new SequenceIR([token(Comma), reference()])), labelled("value", token(Ident))])),
            "SP" => token(Space),
            "held:(COMMA (SEMI | LP) RP)?" => labelled("held", new OptionalIR(new SequenceIR(
                [token(Comma), new AlternationIR([token(Semi), token(Lp)]), token(Rp)]))),
            "first:Args (LP (COMMA rest:Args)* RP)?" => new SequenceIR(
            [
                labelled("first", reference()),
                new OptionalIR(new SequenceIR([token(Lp), new ZeroOrMoreIR(new SequenceIR([token(Comma), labelled("rest", reference())])), token(Rp)])),
            ]),
            "(key:IDENT COMMA? value:IDENT)*" => new ZeroOrMoreIR(new SequenceIR(
                [labelled("key", token(Ident)), new OptionalIR(token(Comma)), labelled("value", token(Ident))])),
            "first:Args (COMMA (LP Args RP))*" => new SequenceIR(
                [labelled("first", reference()), new ZeroOrMoreIR(new SequenceIR([token(Comma), new SequenceIR([token(Lp), reference(), token(Rp)])]))]),
            "Args?" => new OptionalIR(reference()),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
        };

        return new RuleAlternativeIR(null, labels, new SequenceIR([labelled("name", token(Ident)), body]));
    }
}