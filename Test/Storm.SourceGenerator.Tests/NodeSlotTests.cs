// Every property of a generated node names the element that fills it, so the parser writes each value where the AST
// declares it. An element under an optional fills a nullable property, one under a repetition a list, at any depth;
// nothing the parser reads is left without a property.

using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Tests;

[Trait("Tier", "1")]
public sealed class NodeSlotTests
{
    private static readonly TokenIR Ident = new("IDENT", new StringLiteralIR("x"), isHidden: false, declarationIndex: 0);
    private static readonly TokenIR Comma = new("COMMA", new StringLiteralIR(","), isHidden: false, declarationIndex: 1);
    private static readonly TokenIR Lt = new("LT", new StringLiteralIR("<"), isHidden: false, declarationIndex: 2);
    private static readonly TokenIR Gt = new("GT", new StringLiteralIR(">"), isHidden: false, declarationIndex: 3);
    private static readonly RuleIR Elem = new("Elem", [new RuleAlternativeIR(null, [], new TokenRefIR(Ident))]);

    [Theory]
    [InlineData("name:IDENT Elem?", "Name=name Elem=Elem")]
    [InlineData("name:IDENT (COMMA Elem)?", "Name=name Elem=Elem")]
    [InlineData("first:Elem (LT (COMMA rest:Elem)* GT)?", "First=first Rest=rest GT=GT Elems+=rest")]
    [InlineData("first:Elem (COMMA Elem?)*", "First=first COMMAs+=COMMA Elems+=Elem")]
    [InlineData("first:Elem (COMMA (LT Elem GT))*", "First=first COMMAs+=COMMA Elems+=Elem")]
    [InlineData("first:Elem (COMMA more:Elem?)*", "First=first More+=more COMMAs+=COMMA")]
    [InlineData("name:IDENT held:(COMMA inner:Elem)?", "Name=name Inner=inner")]
    [InlineData("name:IDENT held:(COMMA Elem)?", "Name=name Held=Elem")]
    public void EachPropertyIsFilledByTheElementItHolds(string shape, string filled)
    {
        var (alternative, names) = Alternative(shape);

        var slots = AstEmitter.NodeSlots(new RuleIR("Item", [alternative]), alternative, "ItemNode");

        Assert.Equal(filled, string.Join(" ", slots.Select(slot => slot.Name + (slot.Appends ? "+=" : "=") + names[slot.Source])));
    }

    // Builds the shape directly: a label names its element, an unlabelled element is named after its token or rule.
    private static (RuleAlternativeIR Alternative, Dictionary<ExpressionIR, string> Names) Alternative(string shape)
    {
        var names = new Dictionary<ExpressionIR, string>();
        var labels = new List<FieldLabelIR>();

        TokenRefIR token(TokenIR kind, string? label = null)
        {
            var tokenRef = new TokenRefIR(kind);
            names[tokenRef] = label ?? kind.Name;
            if(label != null)
            {
                labels.Add(new FieldLabelIR(label, tokenRef));
            }

            return tokenRef;
        }

        RuleRefIR reference(string? label = null)
        {
            var ruleRef = new RuleRefIR(Elem);
            names[ruleRef] = label ?? "Elem";
            if(label != null)
            {
                labels.Add(new FieldLabelIR(label, ruleRef));
            }

            return ruleRef;
        }

        ExpressionIR labelled(string label, ExpressionIR expr)
        {
            labels.Add(new FieldLabelIR(label, expr));
            return expr;
        }

        ExpressionIR body = shape switch
        {
            "name:IDENT Elem?" => new SequenceIR([token(Ident, "name"), new OptionalIR(reference())]),
            "name:IDENT (COMMA Elem)?" => new SequenceIR([token(Ident, "name"), new OptionalIR(new SequenceIR([token(Comma), reference()]))]),
            "first:Elem (LT (COMMA rest:Elem)* GT)?" => new SequenceIR(
            [
                reference("first"),
                new OptionalIR(new SequenceIR([token(Lt), new ZeroOrMoreIR(new SequenceIR([token(Comma), reference("rest")])), token(Gt)])),
            ]),
            "first:Elem (COMMA Elem?)*" => new SequenceIR([reference("first"), new ZeroOrMoreIR(new SequenceIR([token(Comma), new OptionalIR(reference())]))]),
            "first:Elem (COMMA more:Elem?)*" => new SequenceIR([reference("first"), new ZeroOrMoreIR(new SequenceIR([token(Comma), new OptionalIR(reference("more"))]))]),
            "first:Elem (COMMA (LT Elem GT))*" => new SequenceIR(
            [
                reference("first"),
                new ZeroOrMoreIR(new SequenceIR([token(Comma), new SequenceIR([token(Lt), reference(), token(Gt)])])),
            ]),
            "name:IDENT held:(COMMA inner:Elem)?" => new SequenceIR([token(Ident, "name"), labelled("held", new OptionalIR(new SequenceIR([token(Comma), reference("inner")])))]),
            "name:IDENT held:(COMMA Elem)?" => new SequenceIR([token(Ident, "name"), labelled("held", new OptionalIR(new SequenceIR([token(Comma), reference()])))]),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
        };

        return (new RuleAlternativeIR(null, labels, body), names);
    }
}