using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Unit tests for trait interface emission and node class implements lists.
/// Validates: Requirements 6.1, 6.2, 7.1, 7.2
/// </summary>
public class TraitEmitterTests
{
    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

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
    /// Creates an EmissionContext with full trait IR building.
    /// Runs the complete pipeline: parse → lower → build trait IR → create context.
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

        var traitResult = TraitIRBuilder.Build(stormGrammar);

        return new EmissionContext(
            ns, grammarName, loweringResult.Grammar!,
            traits: traitResult.Traits,
            ruleTraitBindings: traitResult.RuleTraitBindings);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Simple trait → interface declaration
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void SimpleTrait_EmitsInterfaceWithCorrectName()
    {
        var ctx = CreateContextWithTraits(
            "token Ident = [a-z]+;\nrule Expr = Ident;\ntrait Callable = body:Expr;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("public interface ICallableNode", output);
    }

    [Fact]
    public void SimpleTrait_EmitsPropertyWithRuleNodeType()
    {
        var ctx = CreateContextWithTraits(
            "token Ident = [a-z]+;\nrule Expr = Ident;\ntrait Callable = body:Expr;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("ExprNode Body { get; }", output);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Token field type
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void TraitWithTokenField_EmitsTokenPropertyType()
    {
        var ctx = CreateContextWithTraits(
            "token Ident = [a-z]+;\ntrait Named = name:Ident;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("public interface INamedNode", output);
        Assert.Contains("Token Name { get; }", output);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Rule field type
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void TraitWithRuleField_EmitsRuleNodePropertyType()
    {
        var ctx = CreateContextWithTraits(
            "token Num = [0-9]+;\nrule Body = Num;\ntrait HasBody = body:Body;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("public interface IHasBodyNode", output);
        Assert.Contains("BodyNode Body { get; }", output);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Optional cardinality (?)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void TraitWithOptionalTokenField_EmitsNullableTokenType()
    {
        var ctx = CreateContextWithTraits(
            "token Semi = \";\";\ntrait MaybeSemi = semi:Semi?;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("Token? Semi { get; }", output);
    }

    [Fact]
    public void TraitWithOptionalRuleField_EmitsNullableNodeType()
    {
        var ctx = CreateContextWithTraits(
            "token Num = [0-9]+;\nrule Body = Num;\ntrait Opt = body:Body?;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("BodyNode? Body { get; }", output);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Zero-or-more cardinality (*)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void TraitWithZeroOrMoreTokenField_EmitsImmutableArrayToken()
    {
        var ctx = CreateContextWithTraits(
            "token Comma = \",\";\ntrait Many = items:Comma*;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("ImmutableArray<Token> Items { get; }", output);
    }

    [Fact]
    public void TraitWithZeroOrMoreRuleField_EmitsImmutableArrayNode()
    {
        var ctx = CreateContextWithTraits(
            "token Num = [0-9]+;\nrule Stmt = Num;\ntrait Block = stmts:Stmt*;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("ImmutableArray<StmtNode> Stmts { get; }", output);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  One-or-more cardinality (+)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void TraitWithOneOrMoreTokenField_EmitsImmutableArrayToken()
    {
        var ctx = CreateContextWithTraits(
            "token Comma = \",\";\ntrait AtLeastOne = items:Comma+;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("ImmutableArray<Token> Items { get; }", output);
    }

    [Fact]
    public void TraitWithOneOrMoreRuleField_EmitsImmutableArrayNode()
    {
        var ctx = CreateContextWithTraits(
            "token Num = [0-9]+;\nrule Stmt = Num;\ntrait AtLeastOne = stmts:Stmt+;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("ImmutableArray<StmtNode> Stmts { get; }", output);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Trait composition → interface inheritance
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void TraitComposition_EmitsInterfaceWithBaseInterface()
    {
        var ctx = CreateContextWithTraits(
            "token Ident = [a-z]+;\nrule Body = Ident;\ntrait Base = name:Ident;\ntrait Extended : Base = body:Body;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("public interface IBaseNode", output);
        Assert.Contains("public interface IExtendedNode : IBaseNode", output);
    }

    [Fact]
    public void TraitComposition_ExtendedInterfaceOnlyEmitsOwnProperties()
    {
        var ctx = CreateContextWithTraits(
            "token Ident = [a-z]+;\nrule Body = Ident;\ntrait Base = name:Ident;\ntrait Extended : Base = body:Body;");

        var output = AstEmitter.EmitAstNodes(ctx);

        // Find IExtendedNode interface block and ensure it only has 'body' property, not 'name'
        var extStart = output.IndexOf("public interface IExtendedNode");
        Assert.True(extStart >= 0);

        // Find the opening brace after IExtendedNode
        var braceStart = output.IndexOf('{', extStart);
        Assert.True(braceStart >= 0);

        // Find the matching closing brace
        var braceEnd = output.IndexOf('}', braceStart);
        Assert.True(braceEnd >= 0);

        var interfaceBody = output.Substring(braceStart, braceEnd - braceStart + 1);

        Assert.Contains("BodyNode Body { get; }", interfaceBody);
        Assert.DoesNotContain("Token Name { get; }", interfaceBody);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Rule binding trait → sealed class implements interface
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void RuleBindingTrait_SealedClassImplementsInterface()
    {
        var ctx = CreateContextWithTraits(
            "token Ident = [a-z]+;\ntrait Named = name:Ident;\nrule Foo : Named = name:Ident;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("public sealed class FooNode : SyntaxNode, INamedNode", output);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Rule binding multiple traits → class implements all interfaces
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void RuleBindingMultipleTraits_ImplementsAllInterfaces()
    {
        var ctx = CreateContextWithTraits(
            "token Ident = [a-z]+;\ntoken Num = [0-9]+;\ntrait Named = name:Ident;\ntrait Valued = value:Num;\nrule Item : Named, Valued = name:Ident value:Num;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("public sealed class ItemNode : SyntaxNode, INamedNode, IValuedNode", output);
    }
}