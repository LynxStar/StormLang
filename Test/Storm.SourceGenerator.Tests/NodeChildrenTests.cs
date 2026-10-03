using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Tests;

[Trait("Tier", "2")]
public sealed class NodeChildrenTests
{
    private static readonly TokenIR Ident = new("IDENT", new StringLiteralIR("x"), isHidden: false, declarationIndex: 0);
    private static readonly TokenIR Comma = new("COMMA", new StringLiteralIR(","), isHidden: false, declarationIndex: 1);
    private static readonly TokenIR Lt = new("LT", new StringLiteralIR("<"), isHidden: false, declarationIndex: 2);
    private static readonly TokenIR Gt = new("GT", new StringLiteralIR(">"), isHidden: false, declarationIndex: 3);
    private static readonly TokenIR Semi = new("SEMI", new StringLiteralIR(";"), isHidden: false, declarationIndex: 4);
    private static readonly RuleIR Elem = new("Elem", [new RuleAlternativeIR(null, [], new TokenRefIR(Ident))]);

    [Theory]
    [InlineData("name:IDENT held:(COMMA inner:Elem)?", "if (Inner != null) { yield return Inner; }")]
    [InlineData("name:IDENT held:(COMMA Elem)?", "if (Held != null) { yield return Held; }")]
    [InlineData("held:(inner:Elem)?", "if (Inner != null) { yield return Inner; }")]
    [InlineData("name:IDENT (COMMA inner:Elem)?", "if (Inner != null) { yield return Inner; }")]
    [InlineData("held:(COMMA inner:Elem)*", "foreach (var child in Held) { yield return child; }")]
    [InlineData("(COMMA held:(LT inner:Elem GT)?)*", "foreach (var child in Inner) { yield return child; }")]
    [InlineData("name:IDENT held:(COMMA first:Elem second:Elem)?", "if (Group0 != null) { yield return Group0.Value.First; yield return Group0.Value.Second; }")]
    [InlineData("name:IDENT held:(COMMA Elem second:Elem)?", "if (Group0 != null) { yield return Group0.Value.Elem; yield return Group0.Value.Second; }")]
    [InlineData("held:(COMMA first:Elem second:Elem)*", "foreach (var groupItem in Group0) { yield return groupItem.First; yield return groupItem.Second; }")]
    [InlineData("name:IDENT held:(COMMA first:Elem rest:Elem*)?", "if (Group0 != null) { yield return Group0.Value.First; foreach (var child in Group0.Value.Rest) { yield return child; } }")]
    [InlineData("(first:Elem more:Elem? rest:Elem*)*", "foreach (var groupItem in Group0) { yield return groupItem.First; if (groupItem.More != null) { yield return groupItem.More; } foreach (var child in groupItem.Rest) { yield return child; } }")]
    [InlineData("head:IDENT (key:IDENT (LT value:Elem GT)? SEMI)*", "foreach (var groupItem in Group0) { if (groupItem.Group0 != null) { yield return groupItem.Group0.Value.Value; } }")]
    [InlineData("(first:Elem (COMMA rest:Elem)*)*", "foreach (var groupItem in Group0) { yield return groupItem.First; foreach (var groupItem1 in groupItem.Group0) { yield return groupItem1.Rest; } }")]
    [InlineData("head:Elem (key:IDENT COMMA? value:IDENT)*", "yield return Head;")]
    [InlineData("a:Elem COMMA | SEMI", "if (A != null) { yield return A; }")]
    [InlineData("a:Elem COMMA | a:Elem SEMI", "yield return A;")]
    [InlineData("a:Elem COMMA | SEMI (COMMA a:Elem)*", "if (A != null) { yield return A; }")]
    [InlineData("a:Elem COMMA | SEMI (COMMA a:Elem)+", "yield return A;")]
    public void ChildrenYieldsEachNodeTheNodeHoldsOnce(string shape, string children)
    {
        var item = new RuleIR("Item", shape.Split(" | ").Select(Alternative).ToList());
        var ast = AstEmitter.EmitAstNodes(new EmissionContext("Test.Generated", "Test", new GrammarIR([Ident, Comma, Lt, Gt, Semi], [Elem, item]), emitObsoleteShims: false));

        Assert.Equal(children, ChildrenOf(ast, "ItemNode"));
    }

    private static RuleAlternativeIR Alternative(string shape)
    {
        var labels = new List<FieldLabelIR>();

        TokenRefIR token(TokenIR kind, string? label = null)
        {
            return (TokenRefIR)labelled(label, new TokenRefIR(kind));
        }

        RuleRefIR reference(string? label = null)
        {
            return (RuleRefIR)labelled(label, new RuleRefIR(Elem));
        }

        ExpressionIR labelled(string? label, ExpressionIR expr)
        {
            if(label != null)
            {
                labels.Add(new FieldLabelIR(label, expr));
            }

            return expr;
        }

        var body = shape switch
        {
            "name:IDENT held:(COMMA inner:Elem)?" => new SequenceIR([token(Ident, "name"), labelled("held", new OptionalIR(new SequenceIR([token(Comma), reference("inner")])))]),
            "name:IDENT held:(COMMA Elem)?" => new SequenceIR([token(Ident, "name"), labelled("held", new OptionalIR(new SequenceIR([token(Comma), reference()])))]),
            "held:(inner:Elem)?" => labelled("held", new OptionalIR(reference("inner"))),
            "name:IDENT (COMMA inner:Elem)?" => new SequenceIR([token(Ident, "name"), new OptionalIR(new SequenceIR([token(Comma), reference("inner")]))]),
            "held:(COMMA inner:Elem)*" => labelled("held", new ZeroOrMoreIR(new SequenceIR([token(Comma), reference("inner")]))),
            "(COMMA held:(LT inner:Elem GT)?)*" => new ZeroOrMoreIR(new SequenceIR(
            [
                token(Comma),
                labelled("held", new OptionalIR(new SequenceIR([token(Lt), reference("inner"), token(Gt)]))),
            ])),
            "name:IDENT held:(COMMA first:Elem second:Elem)?" => new SequenceIR(
            [
                token(Ident, "name"),
                labelled("held", new OptionalIR(new SequenceIR([token(Comma), reference("first"), reference("second")]))),
            ]),
            "name:IDENT held:(COMMA Elem second:Elem)?" => new SequenceIR(
            [
                token(Ident, "name"),
                labelled("held", new OptionalIR(new SequenceIR([token(Comma), reference(), reference("second")]))),
            ]),
            "held:(COMMA first:Elem second:Elem)*" => labelled("held", new ZeroOrMoreIR(new SequenceIR([token(Comma), reference("first"), reference("second")]))),
            "name:IDENT held:(COMMA first:Elem rest:Elem*)?" => new SequenceIR(
            [
                token(Ident, "name"),
                labelled("held", new OptionalIR(new SequenceIR([token(Comma), reference("first"), labelled("rest", new ZeroOrMoreIR(reference()))]))),
            ]),
            "(first:Elem more:Elem? rest:Elem*)*" => new ZeroOrMoreIR(new SequenceIR(
            [
                reference("first"),
                labelled("more", new OptionalIR(reference())),
                labelled("rest", new ZeroOrMoreIR(reference())),
            ])),
            "head:IDENT (key:IDENT (LT value:Elem GT)? SEMI)*" => new SequenceIR(
            [
                token(Ident, "head"),
                new ZeroOrMoreIR(new SequenceIR([token(Ident, "key"), new OptionalIR(new SequenceIR([token(Lt), reference("value"), token(Gt)])), token(Semi)])),
            ]),
            "(first:Elem (COMMA rest:Elem)*)*" => new ZeroOrMoreIR(new SequenceIR([reference("first"), new ZeroOrMoreIR(new SequenceIR([token(Comma), reference("rest")]))])),
            "head:Elem (key:IDENT COMMA? value:IDENT)*" => new SequenceIR(
            [
                reference("head"),
                new ZeroOrMoreIR(new SequenceIR([token(Ident, "key"), new OptionalIR(token(Comma)), token(Ident, "value")])),
            ]),
            "a:Elem COMMA" => new SequenceIR([reference("a"), token(Comma)]),
            "a:Elem SEMI" => new SequenceIR([reference("a"), token(Semi)]),
            "SEMI" => token(Semi),
            "SEMI (COMMA a:Elem)*" => new SequenceIR([token(Semi), new ZeroOrMoreIR(new SequenceIR([token(Comma), reference("a")]))]),
            "SEMI (COMMA a:Elem)+" => new SequenceIR([token(Semi), new OneOrMoreIR(new SequenceIR([token(Comma), reference("a")]))]),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
        };

        return new RuleAlternativeIR(null, labels, body);
    }

    private static string ChildrenOf(string ast, string nodeTypeName)
    {
        var node = ast[ast.IndexOf($"public sealed class {nodeTypeName} ", StringComparison.Ordinal)..];
        var open = node.IndexOf('{', node.IndexOf("Children()", StringComparison.Ordinal));
        var depth = 0;
        var close = open;
        do
        {
            depth += node[close] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0,
            };
            close++;
        }
        while(depth > 0);

        return string.Join(" ", node[(open + 1)..(close - 1)].Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));
    }
}