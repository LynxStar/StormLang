using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Preservation tests — verify that the bugfix did NOT break existing behavior
/// for grammar patterns that were already working correctly before the fix.
/// Covers simple ZeroOrMoreIR(RuleRefIR), simple OptionalIR(TokenRefIR),
/// simple OptionalIR(RuleRefIR), named alternatives, and rules with no
/// repetitions or optionals.
/// </summary>
public class PreservationTests
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
    //  6.1 — Simple ZeroOrMoreIR(RuleRefIR): Expr* → ImmutableArray<ExprNode>
    // ---------------------------------------------------------------

    [Fact]
    public void AstEmitter_SimpleZeroOrMoreRuleRef_GeneratesImmutableArray()
    {
        // rule Start = Expr*;
        // Simple ZeroOrMoreIR(RuleRefIR) should continue to generate ImmutableArray<ExprNode>
        var ctx = CreateContext(
            "token NUMBER = [0-9]+;\n" +
            "rule Expr = NUMBER;\n" +
            "rule Start = Expr*;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("ImmutableArray<ExprNode>", output);
    }

    [Fact]
    public void AstEmitter_SimpleZeroOrMoreRuleRef_ChildrenYieldsListItems()
    {
        var ctx = CreateContext(
            "token NUMBER = [0-9]+;\n" +
            "rule Expr = NUMBER;\n" +
            "rule Start = Expr*;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("public sealed class StartNode : SyntaxNode", output);
        Assert.Contains("foreach (var child in", output);
    }

    [Fact]
    public void ParserEmitter_SimpleZeroOrMoreRuleRef_AccumulatesNodes()
    {
        var ctx = CreateContext(
            "token NUMBER = [0-9]+;\n" +
            "rule Expr = NUMBER;\n" +
            "rule Start = Expr*;");

        var output = ParserEmitter.Emit(ctx);

        // Parser should call _ParseExpr() and accumulate results
        Assert.Contains("_ParseExpr()", output);
        Assert.Contains(".ToImmutable()", output);
    }

    // ---------------------------------------------------------------
    //  6.2 — Simple OptionalIR(TokenRefIR): Ident? → Token?
    // ---------------------------------------------------------------

    [Fact]
    public void AstEmitter_SimpleOptionalTokenRef_GeneratesNullableToken()
    {
        // rule Expr = Number Ident?;
        // Simple OptionalIR(TokenRefIR) should continue to generate Token?
        var ctx = CreateContext(
            "token Number = [0-9]+;\n" +
            "token Ident = [a-zA-Z]+;\n" +
            "rule Expr = Number Ident?;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("Token?", output);
    }

    [Fact]
    public void AstEmitter_SimpleOptionalTokenRef_ExprNodeIsSealedClass()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\n" +
            "token Ident = [a-zA-Z]+;\n" +
            "rule Expr = Number Ident?;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("public sealed class ExprNode : SyntaxNode", output);
    }

    [Fact]
    public void ParserEmitter_SimpleOptionalTokenRef_EmitsOptionalTokenParsing()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\n" +
            "token Ident = [a-zA-Z]+;\n" +
            "rule Expr = Number Ident?;");

        var output = ParserEmitter.Emit(ctx);

        // Parser should handle the optional Ident token
        Assert.Contains("TokenKind.Ident", output);
    }

    // ---------------------------------------------------------------
    //  6.3 — Simple OptionalIR(RuleRefIR): chain:Chain? → ChainNode?
    // ---------------------------------------------------------------

    [Fact]
    public void AstEmitter_SimpleOptionalRuleRef_GeneratesNullableNodeProperty()
    {
        // rule Start = chain:Chain?;
        // Simple OptionalIR(RuleRefIR) should continue to generate ChainNode?
        var ctx = CreateContext(
            "token IDENT = [a-zA-Z]+;\n" +
            "rule Chain = IDENT;\n" +
            "rule Start = chain:Chain?;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("ChainNode?", output);
    }

    [Fact]
    public void AstEmitter_SimpleOptionalRuleRef_ChildrenYieldsWhenPresent()
    {
        var ctx = CreateContext(
            "token IDENT = [a-zA-Z]+;\n" +
            "rule Chain = IDENT;\n" +
            "rule Start = chain:Chain?;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("public sealed class StartNode : SyntaxNode", output);
        // Should yield the Chain property when non-null
        Assert.Contains("if (Chain != null)", output);
        Assert.Contains("yield return Chain", output);
    }

    [Fact]
    public void ParserEmitter_SimpleOptionalRuleRef_CallsParseChain()
    {
        var ctx = CreateContext(
            "token IDENT = [a-zA-Z]+;\n" +
            "rule Chain = IDENT;\n" +
            "rule Start = chain:Chain?;");

        var output = ParserEmitter.Emit(ctx);

        Assert.Contains("_ParseChain()", output);
    }

    // ---------------------------------------------------------------
    //  6.4 — Named alternatives: abstract base + sealed subtypes
    // ---------------------------------------------------------------

    [Fact]
    public void AstEmitter_NamedAlternatives_GeneratesAbstractBase()
    {
        // rule Expr = Number as Lit | Ident as Var;
        // Named alternatives should continue to generate abstract base + sealed subtypes
        var ctx = CreateContext(
            "token Number = [0-9]+;\n" +
            "token Ident = [a-zA-Z]+;\n" +
            "rule Expr = Number as Lit | Ident as Var;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("public abstract class ExprNode : SyntaxNode", output);
    }

    [Fact]
    public void AstEmitter_NamedAlternatives_GeneratesSealedSubtypes()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\n" +
            "token Ident = [a-zA-Z]+;\n" +
            "rule Expr = Number as Lit | Ident as Var;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("public sealed class LitExprNode : ExprNode", output);
        Assert.Contains("public sealed class VarExprNode : ExprNode", output);
    }

    [Fact]
    public void AstEmitter_NamedAlternatives_SubtypesHaveChildrenOverride()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\n" +
            "token Ident = [a-zA-Z]+;\n" +
            "rule Expr = Number as Lit | Ident as Var;");

        var output = AstEmitter.EmitAstNodes(ctx);

        // Each sealed subtype should have its own Children() override
        // Count occurrences of Children() override — should be at least 2 (one per subtype)
        var childrenCount = output.Split("public override IEnumerable<SyntaxNode> Children()").Length - 1;
        Assert.True(childrenCount >= 2,
            $"Expected at least 2 Children() overrides for named alternatives, found {childrenCount}");
    }

    [Fact]
    public void ParserEmitter_NamedAlternatives_EmitsPegOrderedChoice()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\n" +
            "token Ident = [a-zA-Z]+;\n" +
            "rule Expr = Number as Lit | Ident as Var;");

        var output = ParserEmitter.Emit(ctx);

        // PEG ordered-choice backtracking pattern
        Assert.Contains("var saved = _pos;", output);
        Assert.Contains("_pos = saved;", output);
    }

    // ---------------------------------------------------------------
    //  6.5 — No repetitions or optionals: identical output
    // ---------------------------------------------------------------

    [Fact]
    public void AstEmitter_NoRepetitionsOrOptionals_GeneratesSealedClass()
    {
        // rule Stmt = RETURN Expr SEMI;
        // A rule with no repetitions or optionals should generate a simple sealed class
        var ctx = CreateContext(
            "token RETURN = \"return\";\n" +
            "token NUMBER = [0-9]+;\n" +
            "token SEMI = \";\";\n" +
            "rule Expr = NUMBER;\n" +
            "rule Stmt = RETURN Expr SEMI;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("public sealed class StmtNode : SyntaxNode", output);
    }

    [Fact]
    public void AstEmitter_NoRepetitionsOrOptionals_HasTokenAndNodeProperties()
    {
        var ctx = CreateContext(
            "token RETURN = \"return\";\n" +
            "token NUMBER = [0-9]+;\n" +
            "token SEMI = \";\";\n" +
            "rule Expr = NUMBER;\n" +
            "rule Stmt = RETURN Expr SEMI;");

        var output = AstEmitter.EmitAstNodes(ctx);

        // Should have Token properties for RETURN and SEMI, and ExprNode for Expr
        Assert.Contains("ExprNode", output);
        Assert.Contains("Token", output);
    }

    [Fact]
    public void AstEmitter_NoRepetitionsOrOptionals_ChildrenYieldsNodeRefs()
    {
        var ctx = CreateContext(
            "token RETURN = \"return\";\n" +
            "token NUMBER = [0-9]+;\n" +
            "token SEMI = \";\";\n" +
            "rule Expr = NUMBER;\n" +
            "rule Stmt = RETURN Expr SEMI;");

        var output = AstEmitter.EmitAstNodes(ctx);

        // Children() should yield the Expr node reference
        Assert.Contains("yield return Expr", output);
    }

    [Fact]
    public void ParserEmitter_NoRepetitionsOrOptionals_GeneratesDirectParse()
    {
        var ctx = CreateContext(
            "token RETURN = \"return\";\n" +
            "token NUMBER = [0-9]+;\n" +
            "token SEMI = \";\";\n" +
            "rule Expr = NUMBER;\n" +
            "rule Stmt = RETURN Expr SEMI;");

        var output = ParserEmitter.Emit(ctx);

        // Should have parse methods for both rules
        Assert.Contains("_ParseStmt()", output);
        Assert.Contains("_ParseExpr()", output);
    }

    [Fact]
    public void ParserEmitter_NoRepetitionsOrOptionals_NoBacktracking()
    {
        var ctx = CreateContext(
            "token RETURN = \"return\";\n" +
            "token NUMBER = [0-9]+;\n" +
            "token SEMI = \";\";\n" +
            "rule Expr = NUMBER;\n" +
            "rule Stmt = RETURN Expr SEMI;");

        var output = ParserEmitter.Emit(ctx);

        // Extract the _ParseStmt method body
        var methodStart = output.IndexOf("private static StmtNode? _ParseStmt()");
        Assert.True(methodStart >= 0, "Expected _ParseStmt method in output");
        var methodBody = output[methodStart..];

        // Single-alt rule should NOT have backtracking state save/restore
        Assert.DoesNotContain("var saved = _pos", methodBody);
    }
}