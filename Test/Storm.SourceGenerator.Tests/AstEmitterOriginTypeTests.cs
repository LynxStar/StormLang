using Storm.SourceGenerator.Emit;

namespace Storm.SourceGenerator.Tests;

[Trait("Tier", "1")]
public sealed class AstEmitterOriginTypeTests
{
    [Fact]
    public void RuntimeUsesObjectOriginByDefault()
    {
        Assert.Contains("public object? Origin { get; set; }", AstEmitter.EmitRuntimeTypes());
    }

    [Fact]
    public void RuntimeUsesConsumerOriginTypeWhenProvided()
    {
        Assert.Contains("public Example.Origin? Origin { get; set; }", AstEmitter.EmitRuntimeTypes("Example.Origin"));
    }
}