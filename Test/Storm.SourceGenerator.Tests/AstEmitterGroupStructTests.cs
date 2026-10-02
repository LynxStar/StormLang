using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// AST-emitter output tests for anonymous group struct projection
/// (spec: storm-anonymous-groups, Requirement 9; design Properties 1, 3, 5).
///
/// Each test lowers minimal <c>.storm</c> grammar text through the real lowering
/// stage and asserts on the string returned by
/// <see cref="AstEmitter.EmitAstNodes(EmissionContext)"/>, so field-label
/// reference identity is wired by lowering exactly as in production.
///
/// Capture-count rule under test: a group projects a <c>{Rule}NodeGroup{N}</c>
/// struct IFF it holds two or more value-bearing elements (a labelled element or an
/// unlabelled rule reference; bare fixed tokens are structural and are not counted).
/// A lone alternation group -- pure-token OR mixed -- is a single value-bearing
/// element and therefore does NOT project a struct.
///
/// Task 4.3 (a labelled lone pure-token alternation under <c>*</c>, e.g.
/// <c>(op:(PLUS | MINUS))*</c>) is asserted by
/// <see cref="EmitAstNodes_LonePureTokenAlternationUnderStar_EmitsTokenListAndNoStruct"/>.
/// That shape is one value-bearing element, so it does NOT project a struct; the
/// emitter now promotes it to an <c>ImmutableArray&lt;Token&gt;</c> property on the
/// parent node (design Property 5, Requirement 2.2) rather than flattening it to a
/// scalar <c>Token</c> that would drop the repetition.
/// </summary>
public class AstEmitterGroupStructTests
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

    // The canonical two-value-bearing group: a labelled pure-token alternation
    // (operator:(PLUS | MINUS) -> one Token field) paired with a rule reference
    // (right:Multiplicative -> one node field). Two value-bearing elements => struct.
    private const string AdditiveGrammar =
        "token PLUS = \"+\";\n" +
        "token MINUS = \"-\";\n" +
        "token NUM = [0-9]+;\n" +
        "rule Multiplicative = value:NUM;\n" +
        "rule Additive = left:Multiplicative (operator:(PLUS | MINUS) right:Multiplicative)*;";

    /// <summary>
    /// Task 3.6 (Requirements 1.1, 1.2, 4.3; design Properties 1, 5): the
    /// two-value-bearing group in <c>Additive</c> projects a
    /// <c>public struct AdditiveNodeGroup0</c> carrying a <c>Token Operator</c> field
    /// (the pure-token alternation collapses to a single <c>Token</c>) and a
    /// <c>MultiplicativeNode Right</c> field (the rule reference). The struct is
    /// emitted regardless of the shim flag, so this uses the default (shims enabled).
    /// </summary>
    [Fact]
    public void EmitAstNodes_AdditiveGroup_EmitsGroupStructWithOperatorAndRightFields()
    {
        var output = AstEmitter.EmitAstNodes(CreateContext(AdditiveGrammar));

        Assert.Contains("public struct AdditiveNodeGroup0", output);

        // Slice from the struct declaration so these field assertions cannot be
        // satisfied by the class-level shim properties, which are emitted before the
        // struct and use `=>` rather than a `;`-terminated public field.
        var structOnward = output[output.IndexOf("public struct AdditiveNodeGroup0", StringComparison.Ordinal)..];
        Assert.Contains("public Token Operator;", structOnward);
        Assert.Contains("public MultiplicativeNode Right;", structOnward);
    }

    /// <summary>
    /// Task 3.7 (Requirement 9.1; design Properties 3, 4): with shims DISABLED,
    /// <c>AdditiveNode</c> exposes the new <c>ImmutableArray&lt;AdditiveNodeGroup0&gt;
    /// Group0</c> list property and NONE of the old scalar <c>Operator</c>/<c>Right</c>
    /// (or list <c>Multiplicatives</c>) shim properties. The class body is sliced off
    /// before the sibling struct so the struct's own <c>Token Operator;</c> /
    /// <c>MultiplicativeNode Right;</c> fields cannot mask the absence check.
    /// </summary>
    [Fact]
    public void EmitAstNodes_AdditiveGroup_ShimsDisabled_HasGroup0AndNoScalarShims()
    {
        var output = AstEmitter.EmitAstNodes(CreateContext(AdditiveGrammar, emitObsoleteShims: false));

        var classStart = output.IndexOf("public sealed class AdditiveNode", StringComparison.Ordinal);
        var structStart = output.IndexOf("public struct AdditiveNodeGroup0", StringComparison.Ordinal);
        Assert.True(classStart >= 0, "AdditiveNode class not found");
        Assert.True(structStart > classStart, "AdditiveNodeGroup0 struct not found after the class");

        var classBody = output[classStart..structStart];

        Assert.Contains("ImmutableArray<AdditiveNodeGroup0> Group0", classBody);

        // Shims disabled => no scalar/list shim properties remain on the class.
        Assert.DoesNotContain("public Token Operator", classBody);
        Assert.DoesNotContain("public MultiplicativeNode Right", classBody);
        Assert.DoesNotContain("Multiplicatives", classBody);
    }

    /// <summary>
    /// Task 4.3 (Requirements 2.1, 2.2, 9.3; design Property 5): a labelled LONE
    /// pure-token alternation under <c>*</c> -- <c>(op:(PLUS | MINUS))*</c> -- is a
    /// single value-bearing element, so it does NOT project a
    /// <c>ChoiceNodeGroup0</c> struct (capture-count rule). Because the group carries
    /// the <c>*</c> modifier, the parent <c>ChoiceNode</c> receives an
    /// <c>ImmutableArray&lt;Token&gt; Op</c> property that preserves every matched
    /// operator, rather than the scalar <c>Token Op</c> the emitter previously
    /// produced (which silently dropped the repetition). The property name comes from
    /// the label (<c>op</c> -> <c>Op</c>).
    /// </summary>
    [Fact]
    public void EmitAstNodes_LonePureTokenAlternationUnderStar_EmitsTokenListAndNoStruct()
    {
        var grammar =
            "token PLUS = \"+\";\n" +
            "token MINUS = \"-\";\n" +
            "token NUM = [0-9]+;\n" +
            "rule X = value:NUM;\n" +
            "rule Choice = first:X (op:(PLUS | MINUS))*;";

        var output = AstEmitter.EmitAstNodes(CreateContext(grammar));

        // Lone pure-token alternation = one value-bearing element => no struct.
        Assert.DoesNotContain("ChoiceNodeGroup0", output);

        // The * repetition is preserved: the label promotes to a token list, not a
        // scalar Token that would keep only the last operator.
        Assert.Contains("ImmutableArray<Token> Op { get; set; }", output);
        Assert.DoesNotContain("public Token Op ", output);
    }
}