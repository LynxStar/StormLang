using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Tests verifying that LexerEmitter emits structured diagnostic calls
/// with four arguments (message, filePath, line, column) instead of a
/// single interpolated string embedding position data.
/// </summary>
public class LexerEmitterDiagnosticTests
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

    private readonly string output;

    public LexerEmitterDiagnosticTests()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Start = Number;");
        output = LexerEmitter.Emit(ctx);
    }

    // ---------------------------------------------------------------
    //  Structured diagnostics.Add call tests (Requirement 1.2)
    // ---------------------------------------------------------------

    [Fact]
    public void EmitLexer_ErrorPath_CallsDiagnosticsAddWithFourArguments()
    {
        // The emitted error-handling code should call diagnostics.Add with
        // message, source.Path, startPos.Line, startPos.Column
        Assert.Contains(
            "diagnostics.Add($\"Unexpected character '{ch}'\", source.Path, startPos.Line, startPos.Column)",
            output);
    }

    [Fact]
    public void EmitLexer_ErrorPath_MessageDoesNotEmbedLineAndColumn()
    {
        // The error message should NOT contain embedded position info like
        // "at line {startPos.Line}, column {startPos.Column}"
        Assert.DoesNotContain("at line {startPos.Line}", output);
        Assert.DoesNotContain("column {startPos.Column}\")", output);
    }

    [Fact]
    public void EmitLexer_ErrorPath_PassesSourcePathAsSecondArgument()
    {
        // Verify source.Path is passed as the file path argument
        Assert.Contains("source.Path", output);
    }

    [Fact]
    public void EmitLexer_ErrorPath_PassesStartPosLineAsThirdArgument()
    {
        // The structured call should pass startPos.Line
        Assert.Contains("startPos.Line", output);
    }

    [Fact]
    public void EmitLexer_ErrorPath_PassesStartPosColumnAsFourthArgument()
    {
        // The structured call should pass startPos.Column
        Assert.Contains("startPos.Column", output);
    }

    [Fact]
    public void EmitLexer_ErrorPath_DoesNotUseSingleArgDiagnosticsAdd()
    {
        // There should be no single-argument diagnostics.Add call with an
        // interpolated string that embeds position data
        Assert.DoesNotContain(
            "diagnostics.Add($\"Unexpected character '{ch}' at line",
            output);
    }
}