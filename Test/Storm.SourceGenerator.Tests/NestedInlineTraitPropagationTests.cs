using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;
using Storm.SourceGenerator.Validate;

namespace Storm.SourceGenerator.Tests;

[Trait("Tier", "1")]
public sealed class NestedInlineTraitPropagationTests
{
    [Fact]
    public void ATraitOnTheRuleReachesTheLeafArmsOfANestedBlock_AndNotTheNestedPrefix()
    {
        var bindings = TraitIRBuilder.Build(Grammar()).RuleTraitBindings;

        Assert.Equal(["Leaf", "Other", "Parent"], bindings.Where(binding => binding.Value.Any(trait => trait.Name == "Dotted")).Select(binding => binding.Key).Order());
    }

    [Fact]
    public void ALeafArmOfANestedBlockIsCheckedAgainstTheRulesTrait()
    {
        var diagnostic = Assert.Single(TraitValidationPass.Run(Grammar()));

        Assert.Equal(
            (StormDiagnosticIds.STORM_TRAIT_INLINE_ARM_UNSATISFIED, "Inline arm target rule 'Leaf' does not satisfy trait 'Dotted' bound on rule 'Parent'"),
            (diagnostic.Id, diagnostic.Message));
    }

    private static StormGrammar Grammar()
    {
        var nested = new StormInlineBlockExpr(new StormReferenceExpr("Inner"), [new StormInlineArm(new StormReferenceExpr("Leaf"), "LeafArm", "leaf")], 0, 9, 21);
        var file = new StormFile(null, [],
        [
            token("A", "a"), token("B", "b"), token("C", "c"), token("D", "d"),
            new StormRuleDecl("Outer", labelled("a", "A"), 0, 1, 1),
            new StormRuleDecl("Inner", new StormSequenceExpr([labelled("a", "Outer"), labelled("b", "B")]), 0, 2, 1),
            new StormRuleDecl("Leaf", new StormSequenceExpr([labelled("b", "Inner"), labelled("c", "C")]), 0, 3, 1),
            new StormRuleDecl("Other", new StormSequenceExpr([labelled("a", "Outer"), labelled("d", "D")]), 0, 4, 1),
            new StormTraitDecl("Dotted", [new StormTraitField("d", "D", StormFieldCardinality.One)], [], 0, 5, 1),
            new StormRuleDecl("Parent", new StormInlineBlockExpr(new StormReferenceExpr("Outer"),
                [new StormInlineArm(nested, "NestedArm", "inner"), new StormInlineArm(new StormReferenceExpr("Other"), "OtherArm", "other")], 0, 9, 8), 0, 9, 1,
                TraitBindings: ["Dotted"]),
        ]);

        return StormGrammar.Build(new ImportResolutionResult("root", new Dictionary<string, StormFile> { ["root"] = file }, [new KeyValuePair<string, StormFile>("root", file)], [], true));

        static StormExpression labelled(string label, string name)
        {
            return new StormLabeledExpr(label, new StormReferenceExpr(name));
        }

        static StormTokenDecl token(string name, string text)
        {
            return new StormTokenDecl(name, new StormStringLiteralExpr(text), 0, 1, 1);
        }
    }
}