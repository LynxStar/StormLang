using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Tests;

public class EmitterTests
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
    //  CodeWriter tests (Task 9)
    // ---------------------------------------------------------------

    [Fact]
    public void CodeWriter_WriteLine_ProducesIndentedOutput()
    {
        var writer = new CodeWriter();
        writer.WriteLine("line1");
        writer.Indent();
        writer.WriteLine("line2");
        writer.Dedent();
        writer.WriteLine("line3");

        var output = writer.ToString();
        Assert.Contains("line1", output);
        Assert.Contains("    line2", output);
        Assert.Contains("line3", output);
        // line3 should not be indented
        Assert.DoesNotContain("    line3", output);
    }

    [Fact]
    public void CodeWriter_WriteBlock_ProducesBlockWithBraces()
    {
        var writer = new CodeWriter();
        writer.WriteBlock("public class Foo", () => writer.WriteLine("int x;"));

        var output = writer.ToString();
        Assert.Contains("public class Foo", output);
        Assert.Contains("{", output);
        Assert.Contains("    int x;", output);
        Assert.Contains("}", output);
    }

    [Fact]
    public void CodeWriter_WriteEmptyLine_ProducesBlankLine()
    {
        var writer = new CodeWriter();
        writer.WriteLine("a");
        writer.WriteEmptyLine();
        writer.WriteLine("b");

        var lines = writer.ToString().Split('\n');
        // Should have a blank line between a and b
        Assert.True(lines.Length >= 3);
    }

    [Fact]
    public void CodeWriter_Dedent_DoesNotGoBelowZero()
    {
        var writer = new CodeWriter();
        writer.Dedent(); // Should not throw
        writer.Dedent(); // Should not throw
        writer.WriteLine("test");

        var output = writer.ToString();
        Assert.StartsWith("test", output.TrimStart('\r', '\n'));
    }

    // ---------------------------------------------------------------
    //  EmissionContext tests (Task 9)
    // ---------------------------------------------------------------

    [Fact]
    public void EmissionContext_HoldsCorrectValues()
    {
        var grammar = new GrammarIR(
            new List<TokenIR>(),
            new List<RuleIR>());

        var ctx = new EmissionContext("MyApp.Generated", "MyGrammar", grammar);

        Assert.Equal("MyApp.Generated", ctx.ConsumerNamespace);
        Assert.Equal("MyGrammar", ctx.GrammarName);
        Assert.Same(grammar, ctx.Grammar);
    }

    // ---------------------------------------------------------------
    //  LexerEmitter tests (Task 10)
    // ---------------------------------------------------------------

    [Fact]
    public void LexerEmitter_TokenKindEnum_HasCorrectMembers()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\ntoken Ident = [a-z]+;\nhidden token WS = [ \\t]+;\nrule Start = Number;");

        var output = LexerEmitter.Emit(ctx);

        // Should contain TokenKind enum
        Assert.Contains("public enum TokenKind", output);

        // Should have members for each token in declaration order
        Assert.Contains("Number,", output);
        Assert.Contains("Ident,", output);
        Assert.Contains("WS,", output);

        // Should have EndOfFile and Error
        Assert.Contains("EndOfFile,", output);
        Assert.Contains("Error,", output);
    }

    [Fact]
    public void LexerEmitter_TokenKindEnum_IncludesSyntheticTokens()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Expr = Number \"+\" Number;");

        var output = LexerEmitter.Emit(ctx);

        // Should contain the synthetic token for "+"
        Assert.Contains("__SYNTH_PLUS,", output);
    }

    [Fact]
    public void LexerEmitter_LexerClass_ExistsWithCorrectSignature()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Start = Number;",
            grammarName: "Example");

        var output = LexerEmitter.Emit(ctx);

        // Should contain the lexer class
        Assert.Contains("public static class ExampleLexer", output);

        // Should have the Lex method
        Assert.Contains("public static TokenStream Lex(SourceText source, DiagnosticBag diagnostics)", output);
    }

    [Fact]
    public void LexerEmitter_HiddenTokens_MarkedInHiddenKinds()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nhidden token WS = [ \\t]+;\nrule Start = Number;");

        var output = LexerEmitter.Emit(ctx);

        // Should mark WS as hidden
        Assert.Contains("(int)TokenKind.WS", output);
        Assert.Contains("IsHidden", output);
    }

    [Fact]
    public void LexerEmitter_EmptyGrammar_ProducesValidOutput()
    {
        var ctx = CreateContext("");

        var output = LexerEmitter.Emit(ctx);

        // Should still have TokenKind with EndOfFile and Error
        Assert.Contains("public enum TokenKind", output);
        Assert.Contains("EndOfFile,", output);
        Assert.Contains("Error,", output);
    }

    [Fact]
    public void LexerEmitter_UsesCorrectNamespace()
    {
        var ctx = CreateContext(
            "token Foo = \"foo\";\nrule Start = Foo;",
            ns: "MyApp.Generated");

        var output = LexerEmitter.Emit(ctx);

        Assert.Contains("namespace MyApp.Generated", output);
    }

    // ---------------------------------------------------------------
    //  Regex literal emission tests (Task 5.4)
    // ---------------------------------------------------------------

    [Fact]
    public void LexerEmitter_RegexLiteral_DoesNotEmitRegexUsingDirective()
    {
        // DFA-based lexer compiles regex patterns into the DFA at generation time,
        // so no runtime System.Text.RegularExpressions usage is needed.
        var ctx = CreateContext(
            "token Ident = `[a-zA-Z_]+`;\nrule Start = Ident;");

        var output = LexerEmitter.Emit(ctx);

        Assert.DoesNotContain("using System.Text.RegularExpressions;", output);
    }

    [Fact]
    public void LexerEmitter_NoRegexLiteral_OmitsRegexUsingDirective()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Start = Number;");

        var output = LexerEmitter.Emit(ctx);

        Assert.DoesNotContain("using System.Text.RegularExpressions;", output);
    }

    [Fact]
    public void LexerEmitter_RegexLiteral_EmitsDfaTablesInsteadOfRegex()
    {
        // DFA-based lexer uses transition tables instead of runtime Regex
        var ctx = CreateContext(
            "token Ident = `[a-zA-Z_]+`;\nrule Start = Ident;");

        var output = LexerEmitter.Emit(ctx);

        // Should NOT contain any runtime Regex usage
        Assert.DoesNotContain("RegexOptions.Compiled", output);
        Assert.DoesNotContain("private static readonly Regex _regex_", output);
        // Should contain DFA transition tables
        Assert.Contains("_charClass", output);
        Assert.Contains("_trans_default", output);
        Assert.Contains("_accept_default", output);
    }

    [Fact]
    public void LexerEmitter_RegexLiteral_EmitsTableDrivenLexMethod()
    {
        // DFA-based lexer uses table-driven Lex method instead of per-token TryMatch
        var ctx = CreateContext(
            "token Ident = `[a-zA-Z_]+`;\nrule Start = Ident;");

        var output = LexerEmitter.Emit(ctx);

        // Should NOT contain old per-token matching
        Assert.DoesNotContain(".Match(input.Substring(", output);
        Assert.DoesNotContain("TryMatch_", output);
        // Should contain table-driven lexing
        Assert.Contains("_charClass[(int)ch]", output);
        Assert.Contains("transTable[state, cls]", output);
    }

    // ---------------------------------------------------------------
    //  Structural emission tests (Task 11.2)
    // ---------------------------------------------------------------

    [Fact]
    public void LexerEmitter_EmitsCharClassArray()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Start = Number;");

        var output = LexerEmitter.Emit(ctx);

        Assert.Contains("private static readonly int[] _charClass = new int[]", output);
    }

    [Fact]
    public void LexerEmitter_EmitsTransitionTableForDefaultMode()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Start = Number;");

        var output = LexerEmitter.Emit(ctx);

        Assert.Contains("private static readonly short[,] _trans_default = new short[,]", output);
    }

    [Fact]
    public void LexerEmitter_EmitsAcceptingTableForDefaultMode()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Start = Number;");

        var output = LexerEmitter.Emit(ctx);

        Assert.Contains("private static readonly short[] _accept_default = new short[]", output);
    }

    [Fact]
    public void LexerEmitter_DoesNotContainSystemTextRegularExpressions()
    {
        var ctx = CreateContext(
            "token Ident = `[a-zA-Z_]+`;\ntoken Number = [0-9]+;\nrule Start = Ident;");

        var output = LexerEmitter.Emit(ctx);

        Assert.DoesNotContain("System.Text.RegularExpressions", output);
        Assert.DoesNotContain("using System.Text.RegularExpressions", output);
        Assert.DoesNotContain("new Regex(", output);
    }

    [Fact]
    public void LexerEmitter_DoesNotContainTryMatchMethods()
    {
        var ctx = CreateContext(
            "token Ident = `[a-zA-Z_]+`;\ntoken Number = [0-9]+;\nrule Start = Ident;");

        var output = LexerEmitter.Emit(ctx);

        Assert.DoesNotContain("TryMatch_", output);
    }

    [Fact]
    public void LexerEmitter_EmitsLexMethodWithCorrectSignature()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Start = Number;");

        var output = LexerEmitter.Emit(ctx);

        Assert.Contains("public static TokenStream Lex(SourceText source, DiagnosticBag diagnostics)", output);
    }

    [Fact]
    public void LexerEmitter_EmitsIsHiddenMethodWithCorrectSignature()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Start = Number;");

        var output = LexerEmitter.Emit(ctx);

        Assert.Contains("public static bool IsHidden(int tokenKind)", output);
    }

    [Fact]
    public void LexerEmitter_EmitsGetTokenModeMethodWithCorrectSignature()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Start = Number;");

        var output = LexerEmitter.Emit(ctx);

        Assert.Contains("public static int GetTokenMode(int tokenKind)", output);
    }

    [Fact]
    public void LexerEmitter_EmitsStartStateConstant()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Start = Number;");

        var output = LexerEmitter.Emit(ctx);

        Assert.Contains("private const int _start_default", output);
    }

    [Fact]
    public void LexerEmitter_EmitsModeConstant()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Start = Number;");

        var output = LexerEmitter.Emit(ctx);

        Assert.Contains("private const int Mode_default = 0;", output);
    }

    [Fact]
    public void LexerEmitter_EmitsTokenModesArray()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Start = Number;");

        var output = LexerEmitter.Emit(ctx);

        Assert.Contains("private static readonly int[] TokenModes = new int[]", output);
    }

    // ---------------------------------------------------------------
    //  ParserEmitter tests (Task 11)
    // ---------------------------------------------------------------

    [Fact]
    public void ParserEmitter_GeneratesOneMethodPerRule()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\ntoken Ident = [a-z]+;\nrule Expr = Number;\nrule Stmt = Ident;",
            grammarName: "Example");

        var output = ParserEmitter.Emit(ctx);

        // Should have parse methods for both rules
        Assert.Contains("ParseExpr", output);
        Assert.Contains("ParseStmt", output);
    }

    [Fact]
    public void ParserEmitter_MethodSignatures_AreCorrect()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Expr = Number;",
            grammarName: "Test");

        var output = ParserEmitter.Emit(ctx);

        // Should return the correct node type
        Assert.Contains("public static ExprNode? ParseExpr(TokenStream tokens, DiagnosticBag diagnostics)", output);
    }

    [Fact]
    public void ParserEmitter_ParserClass_HasCorrectName()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Start = Number;",
            grammarName: "Example");

        var output = ParserEmitter.Emit(ctx);

        Assert.Contains("public static class ExampleParser", output);
    }

    [Fact]
    public void ParserEmitter_UsesCorrectNamespace()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Start = Number;",
            ns: "MyApp.Generated");

        var output = ParserEmitter.Emit(ctx);

        Assert.Contains("namespace MyApp.Generated", output);
    }

    [Fact]
    public void ParserEmitter_EmptyGrammar_ProducesEmptyParser()
    {
        var ctx = CreateContext("");

        var output = ParserEmitter.Emit(ctx);

        Assert.Contains("public static class TestParser", output);
        // LastPosition property is always emitted as parser infrastructure
        Assert.Contains("public static int LastPosition => _pos;", output);
        // No public parse methods for empty grammar (only LastPosition is public static)
        var afterClass = output[(output.IndexOf("class TestParser") + 20)..];
        var afterLastPosition = afterClass[(afterClass.IndexOf("LastPosition => _pos;") + 21)..];
        Assert.DoesNotContain("public static", afterLastPosition);
        // No _memo in the emitted parser state
        Assert.DoesNotContain("_memo", output);
    }

    [Fact]
    public void ParserEmitter_MultiAlt_EmitsPegOrderedChoicePattern()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\ntoken Ident = [a-z]+;\nrule Expr = Number as Lit | Ident as Var;");

        var output = ParserEmitter.Emit(ctx);

        // State save preamble
        Assert.Contains("var saved = _pos;", output);
        Assert.Contains("var savedError = _hasError;", output);
        Assert.Contains("var savedDiagCount = _diagnostics?.Count ?? 0;", output);

        // Per-alternative state resets
        Assert.Contains("_pos = saved;", output);
        Assert.Contains("_hasError = false;", output);
        Assert.Contains("_diagnostics?.TruncateTo(savedDiagCount);", output);

        // Epilogue restores _hasError to savedError (not false)
        Assert.Contains("_hasError = savedError;", output);
    }

    [Fact]
    public void ParserEmitter_SingleAlt_EmitsDirectParseWithoutBacktracking()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Stmt = Number;");

        var output = ParserEmitter.Emit(ctx);

        // Extract the internal _ParseStmt method to check its body specifically
        var methodStart = output.IndexOf("private static StmtNode? _ParseStmt()");
        Assert.True(methodStart >= 0, "Expected _ParseStmt method in output");
        var methodBody = output[methodStart..];

        // Single-alt should NOT have backtracking state save/restore
        Assert.DoesNotContain("var saved = _pos", methodBody);
        Assert.DoesNotContain("_hasError = false", methodBody);
        Assert.DoesNotContain("TruncateTo", methodBody);
    }

    [Fact]
    public void ParserEmitter_NoLegacyArtifacts_InEmittedCode()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\ntoken Ident = [a-z]+;\nrule Expr = Number as Lit | Ident as Var;\nrule Stmt = Number;");

        var output = ParserEmitter.Emit(ctx);

        // No LL(1) switch for alt selection
        Assert.DoesNotContain("switch (Peek())", output);
        // No deeper lookahead for alt selection
        Assert.DoesNotContain("Peek(1)", output);
        // No TryParse memoization
        Assert.DoesNotContain("TryParse", output);
        // No _memo dictionary
        Assert.DoesNotContain("_memo", output);
    }

    [Fact]
    public void ParserEmitter_NoMemoInParserState()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Start = Number;");

        var output = ParserEmitter.Emit(ctx);

        Assert.DoesNotContain("_memo", output);
    }

    // ---------------------------------------------------------------
    //  AstEmitter tests (Task 12)
    // ---------------------------------------------------------------

    [Fact]
    public void AstEmitter_RuntimeTypes_ContainsAllRequiredTypes()
    {
        var output = AstEmitter.EmitRuntimeTypes();

        Assert.Contains("namespace Storm.Runtime", output);
        Assert.Contains("public enum TokenChannel", output);
        Assert.Contains("public readonly struct SourceSpan", output);
        Assert.Contains("public readonly struct LinePosition", output);
        Assert.Contains("public readonly struct Token", output);
        Assert.Contains("public class SourceText", output);
        Assert.Contains("public class TokenStream", output);
        Assert.Contains("public abstract class SyntaxNode", output);
        Assert.Contains("public class DiagnosticBag", output);
    }

    [Fact]
    public void AstEmitter_SyntaxNode_HasChildrenMethod()
    {
        var output = AstEmitter.EmitRuntimeTypes();

        Assert.Contains("public abstract IEnumerable<SyntaxNode> Children()", output);
    }

    [Fact]
    public void AstEmitter_SingleAlternativeRule_GeneratesSealedClass()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Expr = Number;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("public sealed class ExprNode : SyntaxNode", output);
    }

    [Fact]
    public void AstEmitter_NamedAlternatives_GeneratesAbstractAndSubtypes()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\ntoken Ident = [a-z]+;\nrule Expr = Number as Literal | Ident as Variable;");

        var output = AstEmitter.EmitAstNodes(ctx);

        // Should have abstract base
        Assert.Contains("public abstract class ExprNode : SyntaxNode", output);

        // Should have sealed subtypes
        Assert.Contains("public sealed class LiteralExprNode : ExprNode", output);
        Assert.Contains("public sealed class VariableExprNode : ExprNode", output);
    }

    [Fact]
    public void AstEmitter_TokenReference_GeneratesTokenProperty()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Expr = Number;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("Token", output);
        Assert.Contains("Number", output);
    }

    [Fact]
    public void AstEmitter_RuleReference_GeneratesNodeProperty()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Expr = Number;\nrule Start = Expr;");

        var output = AstEmitter.EmitAstNodes(ctx);

        // Start should have a property of type ExprNode
        Assert.Contains("ExprNode", output);
    }

    [Fact]
    public void AstEmitter_OptionalElement_GeneratesNullableProperty()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\ntoken Ident = [a-z]+;\nrule Expr = Number Ident?;");

        var output = AstEmitter.EmitAstNodes(ctx);

        // Should have a nullable Token property for the optional Ident
        Assert.Contains("Token?", output);
    }

    [Fact]
    public void AstEmitter_ZeroOrMoreElement_GeneratesImmutableArrayProperty()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Expr = Number;\nrule Start = Expr*;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("ImmutableArray<ExprNode>", output);
    }

    [Fact]
    public void AstEmitter_FieldLabels_GeneratesNamedProperties()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Expr = value:Number;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("Value", output);
    }

    [Fact]
    public void AstEmitter_ChildrenOverride_Exists()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Expr = Number;");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("public override IEnumerable<SyntaxNode> Children()", output);
    }

    [Fact]
    public void AstEmitter_UsesCorrectNamespace()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Expr = Number;",
            ns: "MyApp.Generated");

        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("namespace MyApp.Generated", output);
    }
}