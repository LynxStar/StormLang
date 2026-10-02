using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Unit and integration tests for the DFA lexer pipeline.
/// Covers lexer edge cases and interpolated string mode transitions.
/// </summary>
public class DfaLexerUnitAndIntegrationTests
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
    //  Task 11.3 — Edge case tests
    // ---------------------------------------------------------------

    [Fact]
    public void EmittedLexer_EmptyInput_ProducesOnlyEndOfFile()
    {
        // An empty input should produce just an EndOfFile token.
        // Verify the emitted code structure handles this correctly:
        // the while loop condition (pos < input.Length) is false immediately.
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Start = Number;");

        var output = LexerEmitter.Emit(ctx);

        // The Lex method should have the while loop guard
        Assert.Contains("while (pos < input.Length)", output);
        // And should emit EndOfFile after the loop
        Assert.Contains("TokenKind.EndOfFile", output);
        Assert.Contains("new SourceSpan(pos, 0)", output);
    }

    [Fact]
    public void EmittedLexer_SingleCharToken_ProducesCorrectStructure()
    {
        // A grammar with a single-character token should produce
        // a valid DFA with transition tables.
        var ctx = CreateContext(
            "token A = \"a\";\nrule Start = A;");

        var output = LexerEmitter.Emit(ctx);

        // Should have transition and accepting tables
        Assert.Contains("_trans_default", output);
        Assert.Contains("_accept_default", output);
        // Should have the token in the enum
        Assert.Contains("A,", output);
    }

    [Fact]
    public void EmittedLexer_AllErrorInput_EmitsErrorTokenHandling()
    {
        // When no DFA transition exists and no accepting state is seen,
        // the lexer should emit an Error token and advance by one.
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Start = Number;");

        var output = LexerEmitter.Emit(ctx);

        // Should contain error handling logic
        Assert.Contains("TokenKind.Error", output);
        Assert.Contains("Unexpected character", output);
        Assert.Contains("pos++;", output);
    }

    [Fact]
    public void EmittedLexer_SingleCharGrammar_HasValidTransitionTable()
    {
        // A grammar matching only "x" should produce a minimal DFA
        var ctx = CreateContext(
            "token X = \"x\";\nrule Start = X;");

        var output = LexerEmitter.Emit(ctx);

        // The transition table should exist and be well-formed
        Assert.Contains("_trans_default = new short[,]", output);
        Assert.Contains("_accept_default = new short[]", output);
        // The accepting table should exist
        Assert.Contains("_accept_default = new short[]", output);
    }

    [Fact]
    public void EmittedLexer_MultipleTokens_HasCorrectEnumOrder()
    {
        var ctx = CreateContext(
            "token Alpha = [a-z]+;\ntoken Digit = [0-9]+;\nhidden token WS = \" \";\nrule Start = Alpha;");

        var output = LexerEmitter.Emit(ctx);

        // Tokens should appear in declaration order in the enum
        var alphaPos = output.IndexOf("Alpha,");
        var digitPos = output.IndexOf("Digit,");
        var wsPos = output.IndexOf("WS,");
        var eofPos = output.IndexOf("EndOfFile,");
        var errorPos = output.IndexOf("Error,");

        Assert.True(alphaPos < digitPos, "Alpha should come before Digit");
        Assert.True(digitPos < wsPos, "Digit should come before WS");
        Assert.True(wsPos < eofPos, "WS should come before EndOfFile");
        Assert.True(eofPos < errorPos, "EndOfFile should come before Error");
    }

    [Fact]
    public void EmittedLexer_DeeplyNestedModePushPop_EmitsModeStackLogic()
    {
        // A grammar with push/pop mode actions should emit mode stack logic.
        // This tests that deeply nested mode transitions are structurally correct.
        var grammar = @"
token LBRACE = ""{"" => push default;
token RBRACE = ""}"" => pop;
token INTERP_START = ""$\"""" => push Interpolation;

mode Interpolation {
    token TEXT = `[^""\\{]+`;
    token OPEN_BRACE = ""{"" => push default;
    token INTERP_END = ""\"""" => pop;
}

token IDENT = `[a-zA-Z]+`;
rule Start = IDENT;
";

        var ctx = CreateContext(grammar);
        var output = LexerEmitter.Emit(ctx);

        // Should have mode stack initialization
        Assert.Contains("modeStack", output);
        Assert.Contains("modeStack.Push(Mode_default)", output);

        // Should have mode constants for both modes
        Assert.Contains("Mode_default", output);
        Assert.Contains("Mode_Interpolation", output);

        // Should have transition tables for both modes
        Assert.Contains("_trans_default", output);
        Assert.Contains("_trans_Interpolation", output);
        Assert.Contains("_accept_default", output);
        Assert.Contains("_accept_Interpolation", output);

        // Should have push/pop actions in the switch
        Assert.Contains("modeStack.Push(", output);
        Assert.Contains("modeStack.Pop()", output);

        // Should have mode table selection switch
        Assert.Contains("switch (currentMode)", output);
        Assert.Contains("case Mode_default:", output);
        Assert.Contains("case Mode_Interpolation:", output);
    }

    [Fact]
    public void EmittedLexer_PopModeGuardsAgainstUnderflow()
    {
        // Pop mode should only pop if stack has more than one entry
        var grammar = @"
token RBRACE = ""}"" => pop;
token IDENT = `[a-zA-Z]+`;
rule Start = IDENT;
";

        var ctx = CreateContext(grammar);
        var output = LexerEmitter.Emit(ctx);

        // Should guard against stack underflow
        Assert.Contains("modeStack.Count > 1", output);
    }

    // ---------------------------------------------------------------
    //  Task 11.5 — Integration test: Interpolated string mode transitions
    // ---------------------------------------------------------------

    [Fact]
    public void InterpolatedString_EmitsCorrectModeTransitions()
    {
        // Create a grammar with interpolated string support (push/pop modes)
        var grammar = @"
hidden token WS = "" "";
token IDENT = `[a-zA-Z_][a-zA-Z0-9_]*`;
token LBRACE = ""{"" => push default;
token RBRACE = ""}"" => pop;
token INTERP_START = ""$\"""" => push Interpolation;

mode Interpolation {
    token INTERP_TEXT = `[^""\\{]+`;
    token INTERP_OPEN = ""{"" => push default;
    token INTERP_END = ""\"""" => pop;
}

rule Start = IDENT;
";

        var ctx = CreateContext(grammar);
        var output = LexerEmitter.Emit(ctx);

        // Verify mode constants
        Assert.Contains("private const int Mode_default = 0;", output);
        Assert.Contains("private const int Mode_Interpolation = 1;", output);

        // Verify per-mode transition tables exist
        Assert.Contains("_trans_default", output);
        Assert.Contains("_trans_Interpolation", output);
        Assert.Contains("_accept_default", output);
        Assert.Contains("_accept_Interpolation", output);

        // Verify start state constants for each mode
        Assert.Contains("_start_default", output);
        Assert.Contains("_start_Interpolation", output);

        // Verify mode stack push/pop actions are emitted
        // INTERP_START pushes Interpolation mode
        Assert.Contains("modeStack.Push(Mode_Interpolation)", output);
        // INTERP_OPEN pushes default mode
        Assert.Contains("modeStack.Push(Mode_default)", output);
        // Pop actions for RBRACE and INTERP_END
        Assert.Contains("modeStack.Pop()", output);

        // Verify mode table selection switch
        Assert.Contains("switch (currentMode)", output);
        Assert.Contains("case Mode_default:", output);
        Assert.Contains("case Mode_Interpolation:", output);

        // Verify the switch sets the correct tables for each mode
        Assert.Contains("transTable = _trans_default;", output);
        Assert.Contains("transTable = _trans_Interpolation;", output);
        Assert.Contains("acceptTable = _accept_default;", output);
        Assert.Contains("acceptTable = _accept_Interpolation;", output);
    }

}
