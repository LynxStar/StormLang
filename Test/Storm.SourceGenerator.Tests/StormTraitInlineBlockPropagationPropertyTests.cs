using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Property-based tests for inline block trait propagation.
/// Feature: storm-trait-keyword, Property 7: Inline block propagation
///
/// **Validates: Requirement 8**
///
/// When a rule with trait bindings contains an inline block, each arm's target rule
/// SHALL have the trait interface emitted on its node class. The trait interface SHALL
/// NOT be on the wrapper/arm node but on the target rule's node.
/// </summary>
public class StormTraitInlineBlockPropagationPropertyTests
{
    // ══════════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════════

    private static Func<string, string?> InMemoryFileReader(Dictionary<string, string> files)
    {
        return path =>
        {
            var normalized = StormImportResolver.CanonicalizePath(path);
            if(files.TryGetValue(normalized, out var contents))
            {
                return contents;
            }

            return null;
        };
    }

    /// <summary>
    /// Builds an EmissionContext with full trait IR wiring from a grammar source string.
    /// Runs the complete pipeline: parse → resolve → lower → trait IR build → context.
    /// </summary>
    private static EmissionContext CreateContextWithTraits(string grammar, string grammarName = "Test", string ns = "Test.Generated")
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar " + grammarName + ";\n" + grammar
        };

        var resolver = new StormImportResolver(InMemoryFileReader(files));
        var resolution = resolver.Resolve("root/main.storm");
        var stormGrammar = StormGrammar.Build(resolution);
        var loweringResult = GrammarIRLowering.Lower(stormGrammar);

        Assert.True(loweringResult.Succeeded, "Grammar lowering failed: " +
            string.Join(", ", loweringResult.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(loweringResult.Grammar);

        var traitBuildResult = TraitIRBuilder.Build(stormGrammar);

        return new EmissionContext(
            ns, grammarName, loweringResult.Grammar!,
            traits: traitBuildResult.Traits,
            ruleTraitBindings: traitBuildResult.RuleTraitBindings);
    }

    // ══════════════════════════════════════════════════════════════════
    //  Test Case 1: Simple inline block with 2 arms, both targets
    //  satisfy the trait → both targets implement the interface
    //
    //  **Validates: Requirement 8**
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void P7a_InlineBlockPropagation_TraitInterfaceOnTargetRuleNodes()
    {
        // Grammar: trait with field "value", a prefix rule, two target rules
        // that satisfy the trait, and a parent rule with trait binding + inline block.
        var grammar = """
            token A = "a";
            token B = "b";
            token C = "c";
            token D = "d";
            trait HasValue = value:A;
            rule Prefix = B C;
            rule Target1 = value:A Prefix D;
            rule Target2 = value:A Prefix B;
            rule Parent : HasValue = inline Prefix: { Target1 as First, Target2 as Second };
            """;

        var ctx = CreateContextWithTraits(grammar);
        var output = AstEmitter.EmitAstNodes(ctx);

        // The trait interface should exist
        Assert.Contains("public interface IHasValueNode", output);

        // The target rule nodes should implement the trait interface
        Assert.Contains("public sealed class Target1Node : SyntaxNode, IHasValueNode", output);
        Assert.Contains("public sealed class Target2Node : SyntaxNode, IHasValueNode", output);

        // The arm/wrapper nodes should NOT implement the trait interface
        // Arm nodes are named {AltName}{ParentRuleName}Node, i.e. FirstParentNode, SecondParentNode
        Assert.DoesNotContain("FirstParentNode : ParentNode, IHasValueNode", output);
        Assert.DoesNotContain("SecondParentNode : ParentNode, IHasValueNode", output);

        // Parent itself is abstract and should NOT implement the trait
        Assert.DoesNotContain("abstract class ParentNode : SyntaxNode, IHasValueNode", output);
    }

    // ══════════════════════════════════════════════════════════════════
    //  Test Case 2: Target rule has its own direct trait binding PLUS
    //  gets propagated traits → interface appears only once (deduplication)
    //
    //  **Validates: Requirement 8**
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void P7b_InlineBlockPropagation_Deduplication_WhenTargetAlreadyBindsTrait()
    {
        // Grammar: target rule already binds the same trait directly,
        // and the parent rule also propagates it via inline block.
        // The interface should appear only once on the target node.
        var grammar = """
            token A = "a";
            token B = "b";
            token C = "c";
            token D = "d";
            trait HasValue = value:A;
            rule Prefix = B C;
            rule Target1 : HasValue = value:A Prefix D;
            rule Target2 : HasValue = value:A Prefix B;
            rule Parent : HasValue = inline Prefix: { Target1 as First, Target2 as Second };
            """;

        var ctx = CreateContextWithTraits(grammar);
        var output = AstEmitter.EmitAstNodes(ctx);

        // The target nodes should implement the interface exactly once
        Assert.Contains("public sealed class Target1Node : SyntaxNode, IHasValueNode", output);
        Assert.Contains("public sealed class Target2Node : SyntaxNode, IHasValueNode", output);

        // Verify the interface does NOT appear duplicated (check there's no ", IHasValueNode, IHasValueNode")
        Assert.DoesNotContain("IHasValueNode, IHasValueNode", output);
    }

    // ══════════════════════════════════════════════════════════════════
    //  Test Case 3: Multiple trait fields — verify the full interface
    //  is propagated correctly to target rules through inline blocks.
    //
    //  **Validates: Requirement 8**
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void P7c_InlineBlockPropagation_MultipleTraitFields()
    {
        // Grammar with a trait having multiple fields of different cardinalities.
        // Target rules satisfy all fields.
        var grammar = """
            token A = "a";
            token B = "b";
            token C = "c";
            token D = "d";
            rule Inner = C D;
            trait HasFields = name:A items:Inner*;
            rule Prefix = B;
            rule Target1 = name:A items:Inner* Prefix C;
            rule Target2 = name:A items:Inner* Prefix D;
            rule Parent : HasFields = inline Prefix: { Target1 as First, Target2 as Second };
            """;

        var ctx = CreateContextWithTraits(grammar);
        var output = AstEmitter.EmitAstNodes(ctx);

        // Both target nodes implement the interface
        Assert.Contains("public sealed class Target1Node : SyntaxNode, IHasFieldsNode", output);
        Assert.Contains("public sealed class Target2Node : SyntaxNode, IHasFieldsNode", output);

        // The interface has correct properties
        Assert.Contains("Token Name { get; }", output);
        Assert.Contains("ImmutableArray<InnerNode> Items { get; }", output);
    }

    // ══════════════════════════════════════════════════════════════════
    //  Test Case 4: Target rule has its own DIFFERENT trait binding
    //  plus propagated trait — both interfaces appear on target node.
    //
    //  **Validates: Requirement 8**
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void P7d_InlineBlockPropagation_AdditiveWithOwnTraits()
    {
        // Grammar: target rule binds TraitA directly, parent rule propagates TraitB.
        // Target node should implement both ITraitANode and ITraitBNode.
        var grammar = """
            token A = "a";
            token B = "b";
            token C = "c";
            token D = "d";
            trait TraitA = name:A;
            trait TraitB = tag:B;
            rule Prefix = C D;
            rule Target1 : TraitA = name:A tag:B Prefix C;
            rule Target2 : TraitA = name:A tag:B Prefix D;
            rule Parent : TraitB = inline Prefix: { Target1 as First, Target2 as Second };
            """;

        var ctx = CreateContextWithTraits(grammar);
        var output = AstEmitter.EmitAstNodes(ctx);

        // Target nodes should implement BOTH their own trait and the propagated trait
        Assert.Contains("Target1Node : SyntaxNode, ITraitANode, ITraitBNode", output);
        Assert.Contains("Target2Node : SyntaxNode, ITraitANode, ITraitBNode", output);
    }
}