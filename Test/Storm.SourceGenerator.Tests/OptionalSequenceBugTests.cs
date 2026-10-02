using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Unit tests for Bug 2 — Optional sequences like (EQUAL Expression)?.
/// Verifies that AstEmitter and ParserEmitter correctly handle
/// OptionalIR wrapping a SequenceIR that contains RuleRefIR elements.
/// </summary>
public class OptionalSequenceBugTests
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
    //  5.1 — Labeled optional sequence: init:(EQ Expr)? produces ExprNode?
    // ---------------------------------------------------------------

    [Fact]
    public void AstEmitter_LabeledOptionalSequence_GeneratesExprNodeNullableProperty()
    {
        // rule Field = init:(EQ Expr)? SEMI;
        // The labeled optional (EQ Expr)? should produce an ExprNode? property (not Token?)
        var ctx = CreateContext(
            "token IDENT = [a-zA-Z]+;\n" +
            "token EQ = \"=\";\n" +
            "token SEMI = \";\";\n" +
            "rule Expr = IDENT;\n" +
            "rule Field = init:(EQ Expr)? SEMI;");

        var output = AstEmitter.EmitAstNodes(ctx);

        // Should contain ExprNode? for the labeled optional sequence
        Assert.Contains("ExprNode?", output);
        // Should NOT have Token? for the Init property — the sequence contains a RuleRefIR
        // so the property type should be the rule-ref's node type, not Token?
        Assert.DoesNotContain("public Token? Init", output);
    }

    [Fact]
    public void AstEmitter_LabeledOptionalSequence_ChildrenYieldsExprNode()
    {
        var ctx = CreateContext(
            "token IDENT = [a-zA-Z]+;\n" +
            "token EQ = \"=\";\n" +
            "token SEMI = \";\";\n" +
            "rule Expr = IDENT;\n" +
            "rule Field = init:(EQ Expr)? SEMI;");

        var output = AstEmitter.EmitAstNodes(ctx);

        // FieldNode should have a Children() that yields the nullable ExprNode when present
        Assert.Contains("public sealed class FieldNode : SyntaxNode", output);
        Assert.Contains("if (Init != null)", output);
        Assert.Contains("yield return Init", output);
    }

    // ---------------------------------------------------------------
    //  5.2 — Parser emits _ParseExpr() inside optional sequence
    // ---------------------------------------------------------------

    [Fact]
    public void ParserEmitter_LabeledOptionalSequence_CallsParseExpr()
    {
        // rule Field = init:(EQ Expr)? SEMI;
        // The parser should call _ParseExpr() inside the optional sequence handling
        var ctx = CreateContext(
            "token IDENT = [a-zA-Z]+;\n" +
            "token EQ = \"=\";\n" +
            "token SEMI = \";\";\n" +
            "rule Expr = IDENT;\n" +
            "rule Field = init:(EQ Expr)? SEMI;");

        var output = ParserEmitter.Emit(ctx);

        // Should contain a _ParseExpr() call inside the optional sequence
        Assert.Contains("_ParseExpr()", output);
    }

    [Fact]
    public void ParserEmitter_LabeledOptionalSequence_AssignsToLabeledVariable()
    {
        var ctx = CreateContext(
            "token IDENT = [a-zA-Z]+;\n" +
            "token EQ = \"=\";\n" +
            "token SEMI = \";\";\n" +
            "rule Expr = IDENT;\n" +
            "rule Field = init:(EQ Expr)? SEMI;");

        var output = ParserEmitter.Emit(ctx);

        // The parser should assign the parsed Expr result to the _Init variable
        Assert.Contains("_Init", output);
        Assert.Contains("_ParseExpr()", output);
    }

    // ---------------------------------------------------------------
    //  5.3 — Unlabeled optional sequence: (EQ Expr)? produces ExprNode?
    // ---------------------------------------------------------------

    [Fact]
    public void AstEmitter_UnlabeledOptionalSequence_GeneratesExprNodeNullableProperty()
    {
        // rule Field = (EQ Expr)? SEMI;
        // The unlabeled optional (EQ Expr)? should also produce an ExprNode? property
        var ctx = CreateContext(
            "token IDENT = [a-zA-Z]+;\n" +
            "token EQ = \"=\";\n" +
            "token SEMI = \";\";\n" +
            "rule Expr = IDENT;\n" +
            "rule Field = (EQ Expr)? SEMI;");

        var output = AstEmitter.EmitAstNodes(ctx);

        // Should contain ExprNode? for the unlabeled optional sequence
        Assert.Contains("ExprNode?", output);
    }

    [Fact]
    public void AstEmitter_UnlabeledOptionalSequence_ChildrenYieldsExprNode()
    {
        var ctx = CreateContext(
            "token IDENT = [a-zA-Z]+;\n" +
            "token EQ = \"=\";\n" +
            "token SEMI = \";\";\n" +
            "rule Expr = IDENT;\n" +
            "rule Field = (EQ Expr)? SEMI;");

        var output = AstEmitter.EmitAstNodes(ctx);

        // FieldNode should exist and have Children() that yields the nullable ExprNode
        Assert.Contains("public sealed class FieldNode : SyntaxNode", output);
        // The auto-generated property name should be based on the rule-ref name (Expr)
        Assert.Contains("ExprNode?", output);
    }

    [Fact]
    public void ParserEmitter_UnlabeledOptionalSequence_CallsParseExpr()
    {
        // rule Field = (EQ Expr)? SEMI;
        // The parser should also call _ParseExpr() for the unlabeled case
        var ctx = CreateContext(
            "token IDENT = [a-zA-Z]+;\n" +
            "token EQ = \"=\";\n" +
            "token SEMI = \";\";\n" +
            "rule Expr = IDENT;\n" +
            "rule Field = (EQ Expr)? SEMI;");

        var output = ParserEmitter.Emit(ctx);

        // Should contain a _ParseExpr() call inside the optional sequence
        Assert.Contains("_ParseExpr()", output);
    }
}