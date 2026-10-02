// In `first:Elem (COMMA rest:Elem)*` every repeated Elem is collected into a list, and the label also names a
// scalar that the loop overwrites each round. Children() walks the list and not the scalar, which would repeat the
// last element, or yield null when the loop never ran. The scalar itself stays, because the classic and v2 binders
// still read it (backlog/storm.md records the audit).

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
    public void TheListIsAChildAndTheLabelledScalarIsNot(string modifier)
    {
        var children = ChildrenOfList(AstEmitter.EmitAstNodes(ContextOf(List(modifier, separated: true, labelled: true))));

        Assert.Contains("yield return First;", children);
        Assert.Contains("foreach (var child in Elems)", children);
        Assert.DoesNotContain("yield return Rest;", children);
    }

    [Fact]
    [Trait("Tier", "2")]
    public void TheLabelledScalarKeepsItsPropertyAndItsAssignment()
    {
        var context = ContextOf(List("*", separated: true, labelled: true));

        Assert.Contains("public ElemNode Rest { get; set; } = default!;", AstEmitter.EmitAstNodes(context));
        Assert.Contains("node.Rest = _Rest;", ParserEmitter.Emit(context));
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