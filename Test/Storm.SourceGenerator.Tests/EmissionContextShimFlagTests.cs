using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Verifies that the value supplied for the constructor's <c>emitObsoleteShims</c>
/// parameter round-trips to the <see cref="EmissionContext.EmitObsoleteShims"/>
/// property. Scoped to the constructor -> property propagation only (Task 2.4).
/// </summary>
public class EmissionContextShimFlagTests
{
    private static GrammarIR EmptyGrammar()
    {
        return new GrammarIR(new List<TokenIR>(), new List<RuleIR>());
    }

    [Fact]
    public void EmitObsoleteShims_WhenConstructedTrue_PropertyIsTrue()
    {
        var ctx = new EmissionContext(
            "Test.Generated",
            "Test",
            EmptyGrammar(),
            emitObsoleteShims: true);

        Assert.True(ctx.EmitObsoleteShims);
    }

    [Fact]
    public void EmitObsoleteShims_WhenConstructedFalse_PropertyIsFalse()
    {
        var ctx = new EmissionContext(
            "Test.Generated",
            "Test",
            EmptyGrammar(),
            emitObsoleteShims: false);

        Assert.False(ctx.EmitObsoleteShims);
    }

    [Fact]
    public void EmitObsoleteShims_WhenOmitted_DefaultsToTrue()
    {
        var ctx = new EmissionContext(
            "Test.Generated",
            "Test",
            EmptyGrammar());

        Assert.True(ctx.EmitObsoleteShims);
    }
}