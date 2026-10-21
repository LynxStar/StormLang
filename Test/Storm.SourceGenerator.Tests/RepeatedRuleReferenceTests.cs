// In `first:Elem (COMMA rest:Elem)*` every repeated Elem is collected into a list named after the rule, and the label
// names no property of its own: a scalar the loop overwrites each round would hold only the last element, or nothing
// when the loop never ran. A label outside the repetition, like a tuple's required second operand, keeps its scalar.

using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Tests;

public sealed class RepeatedRuleReferenceTests
{
    private static readonly TokenIR Ident = new("IDENT", new StringLiteralIR("x"), isHidden: false, declarationIndex: 0);
    private static readonly TokenIR Comma = new("COMMA", new StringLiteralIR(","), isHidden: false, declarationIndex: 1);
    private static readonly RuleIR Elem = new("Elem", [new RuleAlternativeIR(null, [], new TokenRefIR(Ident))]);

    [Theory]
    [Trait("Tier", "1")]
    [InlineData(false)]
    [InlineData(true)]
    public void ARepetitionCollectsTheRuleReferenceItRepeats(bool separated)
    {
        var repeated = new RuleRefIR(Elem);
        ExpressionIR inner = separated ? new SequenceIR([new TokenRefIR(Comma), repeated]) : repeated;

        Assert.Same(repeated, AstEmitter.RepeatedRuleRef(inner));
    }

    [Fact]
    [Trait("Tier", "1")]
    public void ARepetitionOfTokensCollectsNoRuleReference()
    {
        Assert.Null(AstEmitter.RepeatedRuleRef(new SequenceIR([new TokenRefIR(Comma), new TokenRefIR(Ident)])));
    }

    [Theory]
    [Trait("Tier", "2")]
    [InlineData("*")]
    [InlineData("+")]
    public void TheListIsAChildAndTheLabelAddsNone(string modifier)
    {
        var children = ChildrenOfList(AstEmitter.EmitAstNodes(ContextOf(List(modifier, separated: true, labelled: true))));

        Assert.Contains("yield return First;", children);
        Assert.Contains("foreach (var child in Elems)", children);
        Assert.DoesNotContain("yield return Rest;", children);
    }

    [Theory]
    [Trait("Tier", "2")]
    [InlineData("*", true)]
    [InlineData("*", false)]
    [InlineData("+", true)]
    [InlineData("+", false)]
    public void ALabelOnAReferenceTheRepetitionOverwritesNamesNoScalar(string modifier, bool separated)
    {
        var context = ContextOf(List(modifier, separated, labelled: true));
        var ast = AstEmitter.EmitAstNodes(context);
        var parser = ParserEmitter.Emit(context);

        Assert.Contains("public ImmutableArray<ElemNode> Elems { get; set; }", ast);
        Assert.DoesNotContain("Rest", ast);
        Assert.DoesNotContain("_Rest", parser);
        Assert.Contains("node.Elems = _Elems;", parser);
    }

    [Fact]
    [Trait("Tier", "2")]
    public void ARequiredOperandBeforeTheRepetitionKeepsItsScalar()
    {
        var first = new RuleRefIR(Elem);
        var rest = new RuleRefIR(Elem);
        var additional = new RuleRefIR(Elem);
        var tuple = new RuleIR("List", [new RuleAlternativeIR(null,
            [new FieldLabelIR("first", first), new FieldLabelIR("rest", rest), new FieldLabelIR("additional", additional)],
            new SequenceIR([first, new TokenRefIR(Comma), rest, new ZeroOrMoreIR(new SequenceIR([new TokenRefIR(Comma), additional]))]))]);
        var context = ContextOf(tuple);
        var ast = AstEmitter.EmitAstNodes(context);
        var parser = ParserEmitter.Emit(context);

        Assert.Contains("public ElemNode Rest { get; set; } = default!;", ast);
        Assert.Contains("node.Rest = _Rest;", parser);
        Assert.Contains("node.Elems = _Elems;", parser);
        Assert.DoesNotContain("Additional", ast);
        Assert.Equal(["yield return First;", "yield return Rest;", "foreach (var child in Elems)"],
            ChildrenOfList(ast).Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("yield return", StringComparison.Ordinal) || line.StartsWith("foreach", StringComparison.Ordinal)).Take(3));
    }

    [Theory]
    [Trait("Tier", "2")]
    [InlineData("*", true)]
    [InlineData("*", false)]
    [InlineData("+", true)]
    public void ARuleReferenceRepeatedWithoutASeparatorIsCollectedIntoTheList(string modifier, bool labelled)
    {
        var parser = ParserEmitter.Emit(ContextOf(List(modifier, separated: false, labelled)));

        Assert.Matches(@"_ElemsBuilder\.Add\(_m\d+\);", parser);
        Assert.Contains("node.Elems = _Elems;", parser);
    }

    private static RuleIR List(string modifier, bool separated, bool labelled)
    {
        var first = new RuleRefIR(Elem);
        var repeated = new RuleRefIR(Elem);
        ExpressionIR inner = separated ? new SequenceIR([new TokenRefIR(Comma), repeated]) : repeated;
        ExpressionIR repetition = modifier == "*" ? new ZeroOrMoreIR(inner) : new OneOrMoreIR(inner);
        List<FieldLabelIR> labels = labelled
            ? [new FieldLabelIR("first", first), new FieldLabelIR("rest", repeated)]
            : [new FieldLabelIR("first", first)];

        return new RuleIR("List", [new RuleAlternativeIR(null, labels, new SequenceIR([first, repetition]))]);
    }

    private static EmissionContext ContextOf(RuleIR list)
    {
        return new EmissionContext("Test.Generated", "Test", new GrammarIR([Ident, Comma], [Elem, list]), emitObsoleteShims: false);
    }

    private static string ChildrenOfList(string ast)
    {
        var node = ast[ast.IndexOf("public sealed class ListNode", StringComparison.Ordinal)..];

        return node[node.IndexOf("Children()", StringComparison.Ordinal)..];
    }
}