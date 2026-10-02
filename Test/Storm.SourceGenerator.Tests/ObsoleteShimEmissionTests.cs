using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Obsolete-shim emission tests for anonymous group struct projection
/// (spec: storm-anonymous-groups, Requirement 5; tasks 5.6 and 5.7; design Section 7
/// and Property 8).
///
/// When <see cref="EmissionContext.EmitObsoleteShims"/> is true (the migration
/// default), a struct-projected group emits the old flat-field properties as
/// <c>[Obsolete("Use Group{N}")]</c> shims that forward reads to the new
/// <c>Group{N}</c> data. When it is false, none of those shim properties appear --
/// only the new <c>Group{N}</c> list property.
///
/// Both tests lower the same minimal <c>Additive</c> grammar
/// (<c>left:Multiplicative (operator:(PLUS | MINUS) right:Multiplicative)*</c>) through
/// the real lowering stage and assert on the string returned by
/// <see cref="AstEmitter.EmitAstNodes(EmissionContext)"/>. The <c>AdditiveNode</c>
/// class body is sliced off before the sibling <c>AdditiveNodeGroup0</c> struct so the
/// struct's own public fields (<c>public Token Operator;</c> /
/// <c>public MultiplicativeNode Right;</c>) cannot satisfy -- or mask -- the
/// class-level shim assertions.
/// </summary>
public class ObsoleteShimEmissionTests
{
    private static EmissionContext CreateContext(
        string grammar,
        bool emitObsoleteShims = true,
        string grammarName = "Test",
        string ns = "Test.Generated")
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar " + grammarName + ";\n" + grammar,
        };

        var resolver = new StormImportResolver(path =>
        {
            var normalized = StormImportResolver.CanonicalizePath(path);
            return files.TryGetValue(normalized, out var contents) ? contents : null;
        });

        var resolution = resolver.Resolve("root/main.storm");
        var stormGrammar = StormGrammar.Build(resolution);
        var loweringResult = GrammarIRLowering.Lower(stormGrammar);

        Assert.True(
            loweringResult.Succeeded,
            "Grammar lowering failed: " + string.Join(", ", loweringResult.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(loweringResult.Grammar);

        return new EmissionContext(ns, grammarName, loweringResult.Grammar!, emitObsoleteShims: emitObsoleteShims);
    }

    // The canonical struct-projected group: a labelled pure-token alternation
    // (operator:(PLUS | MINUS) -> one Token field) paired with a rule reference
    // (right:Multiplicative -> one node field). Two value-bearing elements => struct,
    // and (shims enabled) three flat-field shims -- Operator, Right, Multiplicatives.
    private const string AdditiveGrammar =
        "token PLUS = \"+\";\n" +
        "token MINUS = \"-\";\n" +
        "token NUM = [0-9]+;\n" +
        "rule Multiplicative = value:NUM;\n" +
        "rule Additive = left:Multiplicative (operator:(PLUS | MINUS) right:Multiplicative)*;";

    /// <summary>
    /// Task 5.6 (Requirements 5.1, 5.2, 5.3; design Section 7, Property 8): with shims
    /// ENABLED (the default context), the <c>AdditiveNode</c> class body carries the
    /// <c>[Obsolete("Use Group0")]</c> flat-field shims forwarding to the new
    /// <c>Group0</c> data -- the scalar <c>Operator</c> shim (<c>Group0[^1].Operator</c>),
    /// the scalar <c>Right</c> shim (<c>Group0[^1].Right</c>), and the list
    /// <c>Multiplicatives</c> shim (<c>Group0.Select(g => g.Right).ToImmutableArray()</c>).
    /// The class body is sliced off before the sibling <c>AdditiveNodeGroup0</c> struct
    /// so the struct's own <c>public Token Operator;</c> field cannot be what satisfies
    /// the forwarding-body assertion.
    /// </summary>
    [Fact]
    public void EmitAstNodes_AdditiveGroup_ShimsEnabled_EmitsObsoleteForwardingShims()
    {
        var output = AstEmitter.EmitAstNodes(CreateContext(AdditiveGrammar));

        var classStart = output.IndexOf("public sealed class AdditiveNode", StringComparison.Ordinal);
        var structStart = output.IndexOf("public struct AdditiveNodeGroup0", StringComparison.Ordinal);
        Assert.True(classStart >= 0, "AdditiveNode class not found");
        Assert.True(structStart > classStart, "AdditiveNodeGroup0 struct not found after the class");

        var classBody = output[classStart..structStart];

        // The migration shims are [Obsolete]-attributed, pointing consumers at Group0.
        Assert.Contains("[Obsolete(\"Use Group0\")]", classBody);

        // Scalar shim for the operator token: last-wins forward from the group list.
        Assert.Contains("Group0.Length > 0 ? Group0[^1].Operator", classBody);

        // Scalar shim for the right operand node: last-wins forward from the group list.
        Assert.Contains("Group0[^1].Right", classBody);

        // List shim (the old Multiplicatives): projects the Right field from each element.
        Assert.Contains("Group0.Select(g => g.Right).ToImmutableArray()", classBody);
    }

    /// <summary>
    /// Task 5.7 (Requirement 5.5): with shims DISABLED (<c>emitObsoleteShims: false</c>),
    /// the <c>AdditiveNode</c> class body carries ONLY the new <c>Group0</c> list
    /// property -- none of the <c>[Obsolete]</c> flat-field shims. As in task 5.6, the
    /// class body is sliced off before the sibling <c>AdditiveNodeGroup0</c> struct.
    ///
    /// Assertion note (reported to the orchestrator): the absence checks target the
    /// shim PROPERTY DECLARATIONS (<c>public Token Operator</c>,
    /// <c>public MultiplicativeNode Right</c>) rather than the bare identifiers
    /// <c>Operator</c>/<c>Right</c>. A bare <c>Right</c> absence check is a false
    /// expectation: the <c>Children()</c> override legitimately emits
    /// <c>yield return groupItem.Right;</c> (<c>Right</c> is the node-typed group
    /// field), so the substring <c>Right</c> is present in the class body even with
    /// shims removed. The <c>[Obsolete</c> attribute is the definitive shim signal and
    /// is asserted absent directly. This mirrors the proven slice/assertion pattern in
    /// <see cref="AstEmitterGroupStructTests.EmitAstNodes_AdditiveGroup_ShimsDisabled_HasGroup0AndNoScalarShims"/>.
    /// </summary>
    [Fact]
    public void EmitAstNodes_AdditiveGroup_ShimsDisabled_EmitsNoObsoleteShims()
    {
        var output = AstEmitter.EmitAstNodes(CreateContext(AdditiveGrammar, emitObsoleteShims: false));

        var classStart = output.IndexOf("public sealed class AdditiveNode", StringComparison.Ordinal);
        var structStart = output.IndexOf("public struct AdditiveNodeGroup0", StringComparison.Ordinal);
        Assert.True(classStart >= 0, "AdditiveNode class not found");
        Assert.True(structStart > classStart, "AdditiveNodeGroup0 struct not found after the class");

        var classBody = output[classStart..structStart];

        // No shims => no [Obsolete] attribute (the definitive shim signal) and none of
        // the flat-field shim property declarations.
        Assert.DoesNotContain("[Obsolete", classBody);
        Assert.DoesNotContain("public Token Operator", classBody);
        Assert.DoesNotContain("public MultiplicativeNode Right", classBody);
        Assert.DoesNotContain("Multiplicatives", classBody);

        // The new group list property is present.
        Assert.Contains("ImmutableArray<AdditiveNodeGroup0> Group0", classBody);
    }
}