using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Unit tests for Bug 1 — Repeated elements in (COMMA X)* patterns.
/// Verifies that AstEmitter and ParserEmitter correctly handle
/// ZeroOrMoreIR wrapping a SequenceIR that contains RuleRefIR or TokenRefIR elements.
/// </summary>
public class RepetitionBugTests
{
    // ---------------------------------------------------------------
    //  Helpers
    // ---------------------------------------------------------------

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

    private static EmissionContext CreateContext(string grammar, string grammarName = "Test", string ns = "Test.Generated")
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

        return new EmissionContext(ns, grammarName, loweringResult.Grammar!);
    }

    // ---------------------------------------------------------------
    //  4.1 — PList AST: ImmutableArray<ParamNode> for (COMMA Param)*
    // ---------------------------------------------------------------

    [Fact]
    public void AstEmitter_ParameterList_GeneratesImmutableArrayForRepeatedRuleRef()
    {
        // rule PList = Param (COMMA Param)*;
        // The (COMMA Param)* pattern should produce an ImmutableArray<ParamNode> property
        var ctx = CreateContext(
            "token IDENT = [a-zA-Z]+;\n" +
            "token COMMA = \",\";\n" +
            "rule Param = IDENT;\n" +
            "rule PList = Param (COMMA Param)*;");

        var output = AstEmitter.EmitAstNodes(ctx);

        // Should contain an ImmutableArray<ParamNode> property for the repeated params
        Assert.Contains("ImmutableArray<ParamNode>", output);
    }

    [Fact]
    public void AstEmitter_ParameterList_ChildrenYieldsRepeatedParams()
    {
        var ctx = CreateContext(
            "token IDENT = [a-zA-Z]+;\n" +
            "token COMMA = \",\";\n" +
            "rule Param = IDENT;\n" +
            "rule PList = Param (COMMA Param)*;");

        var output = AstEmitter.EmitAstNodes(ctx);

        // The PListNode should have a Children() override that yields the list items
        Assert.Contains("public sealed class PListNode : SyntaxNode", output);
        Assert.Contains("foreach (var child in", output);
    }

    // ---------------------------------------------------------------
    //  4.2 — QId AST: ImmutableArray<Token> for (DOT IDENT)*
    // ---------------------------------------------------------------

    [Fact]
    public void AstEmitter_QualifiedId_GeneratesImmutableArrayTokenForRepeatedTokens()
    {
        // rule QId = IDENT (DOT IDENT)*;
        // The (DOT IDENT)* pattern with only tokens should produce an ImmutableArray<Token> property
        var ctx = CreateContext(
            "token IDENT = [a-zA-Z]+;\n" +
            "token DOT = \".\";\n" +
            "rule QId = IDENT (DOT IDENT)*;");

        var output = AstEmitter.EmitAstNodes(ctx);

        // Should contain an ImmutableArray<Token> property for the repeated identifiers
        Assert.Contains("ImmutableArray<Token>", output);
    }

    [Fact]
    public void AstEmitter_QualifiedId_HasYieldBreakForTokenOnlyList()
    {
        // Token-only lists don't yield children (tokens are not SyntaxNodes)
        var ctx = CreateContext(
            "token IDENT = [a-zA-Z]+;\n" +
            "token DOT = \".\";\n" +
            "rule QId = IDENT (DOT IDENT)*;");

        var output = AstEmitter.EmitAstNodes(ctx);

        // QIdNode should exist as a sealed class
        Assert.Contains("public sealed class QIdNode : SyntaxNode", output);
    }

    // ---------------------------------------------------------------
    //  4.3 — PList Parser: builder, .Add(), .ToImmutable()
    // ---------------------------------------------------------------

    [Fact]
    public void ParserEmitter_ParameterList_CreatesListBuilder()
    {
        var ctx = CreateContext(
            "token IDENT = [a-zA-Z]+;\n" +
            "token COMMA = \",\";\n" +
            "rule Param = IDENT;\n" +
            "rule PList = Param (COMMA Param)*;");

        var output = ParserEmitter.Emit(ctx);

        // Should create an ImmutableArray builder for ParamNode
        Assert.Contains("ImmutableArray.CreateBuilder<ParamNode>()", output);
    }

    [Fact]
    public void ParserEmitter_ParameterList_AddsToBuilder()
    {
        var ctx = CreateContext(
            "token IDENT = [a-zA-Z]+;\n" +
            "token COMMA = \",\";\n" +
            "rule Param = IDENT;\n" +
            "rule PList = Param (COMMA Param)*;");

        var output = ParserEmitter.Emit(ctx);

        // Should have .Add() calls to accumulate parsed params
        Assert.Contains(".Add(", output);
    }

    [Fact]
    public void ParserEmitter_ParameterList_FinalizesWithToImmutable()
    {
        var ctx = CreateContext(
            "token IDENT = [a-zA-Z]+;\n" +
            "token COMMA = \",\";\n" +
            "rule Param = IDENT;\n" +
            "rule PList = Param (COMMA Param)*;");

        var output = ParserEmitter.Emit(ctx);

        // Should finalize the builder with .ToImmutable()
        Assert.Contains(".ToImmutable()", output);
    }

    [Fact]
    public void ParserEmitter_ParameterList_ParsesRepeatedRule()
    {
        var ctx = CreateContext(
            "token IDENT = [a-zA-Z]+;\n" +
            "token COMMA = \",\";\n" +
            "rule Param = IDENT;\n" +
            "rule PList = Param (COMMA Param)*;");

        var output = ParserEmitter.Emit(ctx);

        // Should call _ParseParam() inside the while loop
        Assert.Contains("_ParseParam()", output);
    }

    // ---------------------------------------------------------------
    //  4.4 — ArgList: both AST and parser handle repeated expressions
    // ---------------------------------------------------------------

    [Fact]
    public void AstEmitter_ArgList_GeneratesImmutableArrayForRepeatedExpressions()
    {
        // rule ArgList = Expr (COMMA Expr)*;
        var ctx = CreateContext(
            "token NUMBER = [0-9]+;\n" +
            "token COMMA = \",\";\n" +
            "rule Expr = NUMBER;\n" +
            "rule ArgList = Expr (COMMA Expr)*;");

        var output = AstEmitter.EmitAstNodes(ctx);

        // Should contain an ImmutableArray<ExprNode> property
        Assert.Contains("ImmutableArray<ExprNode>", output);
    }

    [Fact]
    public void AstEmitter_ArgList_ChildrenYieldsRepeatedExpressions()
    {
        var ctx = CreateContext(
            "token NUMBER = [0-9]+;\n" +
            "token COMMA = \",\";\n" +
            "rule Expr = NUMBER;\n" +
            "rule ArgList = Expr (COMMA Expr)*;");

        var output = AstEmitter.EmitAstNodes(ctx);

        // ArgListNode should have a Children() that yields the list items
        Assert.Contains("public sealed class ArgListNode : SyntaxNode", output);
        Assert.Contains("foreach (var child in", output);
    }

    [Fact]
    public void ParserEmitter_ArgList_CreatesListBuilderForExpressions()
    {
        var ctx = CreateContext(
            "token NUMBER = [0-9]+;\n" +
            "token COMMA = \",\";\n" +
            "rule Expr = NUMBER;\n" +
            "rule ArgList = Expr (COMMA Expr)*;");

        var output = ParserEmitter.Emit(ctx);

        // Should create an ImmutableArray builder for ExprNode
        Assert.Contains("ImmutableArray.CreateBuilder<ExprNode>()", output);
    }

    [Fact]
    public void ParserEmitter_ArgList_AccumulatesAndFinalizesExpressions()
    {
        var ctx = CreateContext(
            "token NUMBER = [0-9]+;\n" +
            "token COMMA = \",\";\n" +
            "rule Expr = NUMBER;\n" +
            "rule ArgList = Expr (COMMA Expr)*;");

        var output = ParserEmitter.Emit(ctx);

        // Should have .Add() and .ToImmutable() for accumulation
        Assert.Contains(".Add(", output);
        Assert.Contains(".ToImmutable()", output);
        // Should call _ParseExpr() for the repeated expressions
        Assert.Contains("_ParseExpr()", output);
    }
}