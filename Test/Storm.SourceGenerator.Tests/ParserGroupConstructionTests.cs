using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Emitter-output tests for anonymous-group struct CONSTRUCTION in the generated code
/// (spec: storm-anonymous-groups, Requirement 3 parser construction and Requirement 7.2
/// Children traversal; design Section 5 Children and Section 6 parser patterns).
///
/// Each test lowers minimal <c>.storm</c> grammar text through the real lowering stage
/// (matching <see cref="AstEmitterGroupStructTests"/>) and asserts on the string
/// returned by <see cref="AstEmitter.EmitAstNodes(EmissionContext)"/> or
/// <see cref="ParserEmitter.Emit(EmissionContext)"/>. Assertions are checked against the
/// actual emitted text.
///
/// Capture-count rule: a group projects a <c>{Rule}NodeGroup{N}</c> struct IFF it holds
/// two or more value-bearing elements. The struct-projected groups exercised here are
/// the <c>*</c> group in <c>Additive</c> (Tasks 6.4, 7.6) and the <c>?</c> group in
/// <c>R = head:X (a:Y b:Z)?</c> (Task 8.3).
/// </summary>
public class ParserGroupConstructionTests
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

    /// <summary>
    /// Returns the balanced-brace block that starts at <paramref name="marker"/> (through
    /// the marker's first <c>{ ... }</c>), so an assertion can be scoped to a single
    /// class or method and cannot be satisfied by an unrelated part of the output.
    /// </summary>
    private static string ExtractBalanced(string text, string marker)
    {
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Marker not found: " + marker);

        var braceStart = text.IndexOf('{', start);
        Assert.True(braceStart >= 0, "No opening brace after: " + marker);

        var depth = 0;
        var i = braceStart;
        for(; i < text.Length; i++)
        {
            if(text[i] == '{')
            {
                depth++;
            }
            else if(text[i] == '}')
            {
                depth--;
                if(depth == 0)
                {
                    i++;
                    break;
                }
            }
        }

        return text[start..i];
    }

    // The canonical two-value-bearing `*` group: operator:(PLUS | MINUS) collapses to a
    // Token field, right:Multiplicative is a node field. Two value-bearing => struct.
    private const string AdditiveGrammar =
        "token PLUS = \"+\";\n" +
        "token MINUS = \"-\";\n" +
        "token NUM = [0-9]+;\n" +
        "rule Multiplicative = value:NUM;\n" +
        "rule Additive = left:Multiplicative (operator:(PLUS | MINUS) right:Multiplicative)*;";

    // A `?` group with two value-bearing rule references (a:Y and b:Z) => struct.
    private const string OptionalGroupGrammar =
        "token NUM = [0-9]+;\n" +
        "rule X = value:NUM;\n" +
        "rule Y = value:NUM;\n" +
        "rule Z = value:NUM;\n" +
        "rule R = head:X (a:Y b:Z)?;";

    // A bare (no-modifier) parenthesised group with two value-bearing rule references.
    private const string BareGroupGrammar =
        "token NUM = [0-9]+;\n" +
        "rule X = value:NUM;\n" +
        "rule Y = value:NUM;\n" +
        "rule Z = value:NUM;\n" +
        "rule R = head:X (a:Y b:Z);";

    /// <summary>
    /// Task 6.4 (Requirement 7.2; design Section 5, Property 9): the emitted
    /// <c>AdditiveNode.Children()</c> override iterates the <c>Group0</c> list with a
    /// <c>foreach</c> and yields the node-typed <c>Right</c> field from each group
    /// element. The assertion is scoped to the <c>AdditiveNode</c> class block so the
    /// sibling struct's own <c>Right</c> field cannot satisfy it.
    /// </summary>
    [Fact]
    public void Children_AdditiveGroup_IteratesGroup0AndYieldsRight()
    {
        var output = AstEmitter.EmitAstNodes(CreateContext(AdditiveGrammar));
        var additiveClass = ExtractBalanced(output, "public sealed class AdditiveNode");

        var children = ExtractBalanced(additiveClass, "public override IEnumerable<SyntaxNode> Children()");

        // Iterates the group list ...
        Assert.Contains("foreach (var groupItem in Group0)", children);

        // ... and yields the node-typed Right field from each element.
        Assert.Contains("yield return groupItem.Right;", children);
    }

    /// <summary>
    /// Task 7.6 (Requirement 3.1; design Section 6, Property 6): the emitted parser for
    /// the <c>Additive</c> rule accumulates a <c>{Rule}NodeGroup{N}</c> per loop
    /// iteration -- <c>ImmutableArray.CreateBuilder&lt;AdditiveNodeGroup0&gt;()</c> before
    /// the loop and a <c>new AdditiveNodeGroup0 { ... }</c> constructed inside it -- and
    /// does NOT assign the old scalar <c>node.Operator</c>/<c>node.Right</c> properties
    /// (now get-only <c>[Obsolete]</c> shims). Scoped to the <c>_ParseAdditive</c> method.
    /// </summary>
    [Fact]
    public void ParserAdditiveGroup_BuildsGroupStructPerIterationAndNoScalarAssignments()
    {
        var parser = ParserEmitter.Emit(CreateContext(AdditiveGrammar));
        var method = ExtractBalanced(parser, "private static AdditiveNode? _ParseAdditive");

        Assert.Contains("ImmutableArray.CreateBuilder<AdditiveNodeGroup0>()", method);
        Assert.Contains("new AdditiveNodeGroup0 {", method);

        // The last-wins scalar assignments are gone; the loop stores each iteration in
        // the struct list instead.
        Assert.DoesNotContain("node.Operator", method);
        Assert.DoesNotContain("node.Right", method);
    }

    /// <summary>
    /// Task 8.3 (Requirement 3.2; design Section 6 <c>?</c> pattern): the emitted parser
    /// for a <c>?</c> struct group declares a nullable <c>{Rule}NodeGroup{N}?</c> local,
    /// constructs the struct only when the optional group matches, and assigns the local
    /// to the parent's <c>Group{N}</c> property -- never the old scalar shim properties
    /// (<c>node.A</c>/<c>node.B</c>). Scoped to the <c>_ParseR</c> method.
    /// </summary>
    [Fact]
    public void ParserOptionalGroup_UsesNullableLocalConstructOnMatchAndAssignsGroup()
    {
        var parser = ParserEmitter.Emit(CreateContext(OptionalGroupGrammar));
        var method = ExtractBalanced(parser, "private static RNode? _ParseR");

        // Nullable group local, defaulted to null (absent group stays null).
        Assert.Contains("RNodeGroup0? _group0 = null;", method);

        // Constructed only on a full match ...
        Assert.Contains("_group0 = new RNodeGroup0 {", method);

        // ... and assigned to the parent Group0 property.
        Assert.Contains("node.Group0 = _group0;", method);

        // No assignment to the get-only [Obsolete] scalar shims.
        Assert.DoesNotContain("node.A =", method);
        Assert.DoesNotContain("node.B =", method);
    }

    /// <summary>
    /// Task 8.4 (Requirement 3.3), reconciled to the committed detection behaviour.
    ///
    /// The task specifies a "bare no-modifier struct group" and expects the parser to
    /// emit direct <c>new RNodeGroup0 { ... }</c> construction. Verified against the
    /// actual emitter output, that construction is NOT produced: a parenthesised group
    /// with NO modifier is never reported by
    /// <see cref="AstEmitter.DetectAnonymousGroups"/> -- its <c>WalkForGroups</c> walk
    /// only projects a struct for a group wrapped in <c>*</c>, <c>+</c>, or <c>?</c>
    /// (there is no <c>GroupModifier.None</c> case, matching the design's own
    /// pseudocode). So <c>rule R = head:X (a:Y b:Z);</c> produces flat, settable
    /// <c>A</c>/<c>B</c> node properties and NO <c>RNodeGroup0</c> struct on the AST
    /// side. Because the parser must agree with the AST it cannot construct a struct the
    /// AST does not declare.
    ///
    /// This test therefore asserts the genuine, self-consistent behaviour (flat scalar
    /// assignments, no struct, no builder, no nullable local). The parser side for a
    /// hypothetical no-modifier struct group is still implemented
    /// (<c>EmitBareStructGroup</c>) and would activate automatically if AST-side
    /// detection is ever extended to report such groups. The discrepancy with the task's
    /// stated expectation is reported to the orchestrator rather than forced.
    /// </summary>
    [Fact]
    public void ParserBareGroup_ProducesFlatAssignmentsAndNoStruct_PerCommittedDetection()
    {
        var context = CreateContext(BareGroupGrammar, emitObsoleteShims: false);
        var ast = AstEmitter.EmitAstNodes(context);
        var parser = ParserEmitter.Emit(context);
        var method = ExtractBalanced(parser, "private static RNode? _ParseR");

        // A bare no-modifier group is not struct-projected on the AST side ...
        Assert.DoesNotContain("struct RNodeGroup0", ast);

        // ... so the parser constructs no struct and no accumulator, and uses no
        // nullable group local.
        Assert.DoesNotContain("new RNodeGroup0", method);
        Assert.DoesNotContain("ImmutableArray.CreateBuilder<RNodeGroup0>", method);
        Assert.DoesNotContain("RNodeGroup0?", method);

        // Instead the inner labels stay flat, settable node properties assigned directly.
        Assert.Contains("node.A = _A;", method);
        Assert.Contains("node.B = _B;", method);
    }
}