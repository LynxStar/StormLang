using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Tests verifying that ParserEmitter emits structured diagnostic calls
/// with file path, line, and column arguments instead of embedding
/// position data in the message string.
/// </summary>
public class ParserEmitterDiagnosticTests
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

    public ParserEmitterDiagnosticTests()
    {
        var ctx = CreateContext(
            "token Number = [0-9]+;\nrule Start = Number;");
        output = ParserEmitter.Emit(ctx);
    }

    // ---------------------------------------------------------------
    //  _sourcePath field tests (Requirement 1.3)
    // ---------------------------------------------------------------

    [Fact]
    public void EmitParser_HasThreadStaticSourcePathField()
    {
        // The emitted parser should declare a [ThreadStatic] _sourcePath field
        Assert.Contains("[ThreadStatic] private static string? _sourcePath;", output);
    }

    // ---------------------------------------------------------------
    //  Init method stores source path (Requirement 1.3)
    // ---------------------------------------------------------------

    [Fact]
    public void EmitParser_InitMethod_StoresSourcePath()
    {
        // The Init method should store ts.Source.Path into _sourcePath
        Assert.Contains("_sourcePath = ts.Source.Path;", output);
    }

    // ---------------------------------------------------------------
    //  Require method structured diagnostics (Requirement 1.3)
    // ---------------------------------------------------------------

    [Fact]
    public void EmitParser_RequireMethod_CallsDiagnosticsAddWithStructuredArgs()
    {
        // The Require method should call _diagnostics?.Add with message,
        // _sourcePath, cur.Start.Line, cur.Start.Column
        Assert.Contains(
            "_diagnostics?.Add($\"Expected token kind {kind} but found '{cur.Lexeme}'\", _sourcePath, cur.Start.Line, cur.Start.Column)",
            output);
    }

    [Fact]
    public void EmitParser_RequireMethod_PassesSourcePathAsSecondArgument()
    {
        // Verify _sourcePath is passed in the Require method's diagnostics call
        var requireIndex = output.IndexOf("private static Token Require(int kind)");
        Assert.True(requireIndex >= 0, "Require method not found");

        var requireBody = output.Substring(requireIndex, 500);
        Assert.Contains("_sourcePath", requireBody);
    }

    [Fact]
    public void EmitParser_RequireMethod_PassesCurStartLineAsThirdArgument()
    {
        var requireIndex = output.IndexOf("private static Token Require(int kind)");
        Assert.True(requireIndex >= 0, "Require method not found");

        var requireBody = output.Substring(requireIndex, 500);
        Assert.Contains("cur.Start.Line", requireBody);
    }

    [Fact]
    public void EmitParser_RequireMethod_PassesCurStartColumnAsFourthArgument()
    {
        var requireIndex = output.IndexOf("private static Token Require(int kind)");
        Assert.True(requireIndex >= 0, "Require method not found");

        var requireBody = output.Substring(requireIndex, 500);
        Assert.Contains("cur.Start.Column", requireBody);
    }

    // ---------------------------------------------------------------
    //  Error method structured diagnostics (Requirement 1.3)
    // ---------------------------------------------------------------

    [Fact]
    public void EmitParser_ErrorMethod_CallsDiagnosticsAddWithStructuredArgs()
    {
        // The Error method should call _diagnostics?.Add with message,
        // _sourcePath, cur.Start.Line, cur.Start.Column
        Assert.Contains(
            "_diagnostics?.Add($\"{message}\", _sourcePath, cur.Start.Line, cur.Start.Column)",
            output);
    }

    [Fact]
    public void EmitParser_ErrorMethod_PassesSourcePathAsSecondArgument()
    {
        var errorIndex = output.IndexOf("private static void Error(string message)");
        Assert.True(errorIndex >= 0, "Error method not found");

        var errorBody = output.Substring(errorIndex, 400);
        Assert.Contains("_sourcePath", errorBody);
    }

    [Fact]
    public void EmitParser_ErrorMethod_PassesCurStartLineAsThirdArgument()
    {
        var errorIndex = output.IndexOf("private static void Error(string message)");
        Assert.True(errorIndex >= 0, "Error method not found");

        var errorBody = output.Substring(errorIndex, 400);
        Assert.Contains("cur.Start.Line", errorBody);
    }

    [Fact]
    public void EmitParser_ErrorMethod_PassesCurStartColumnAsFourthArgument()
    {
        var errorIndex = output.IndexOf("private static void Error(string message)");
        Assert.True(errorIndex >= 0, "Error method not found");

        var errorBody = output.Substring(errorIndex, 400);
        Assert.Contains("cur.Start.Column", errorBody);
    }

    // ---------------------------------------------------------------
    //  Old pattern is gone (Requirement 1.3)
    // ---------------------------------------------------------------

    [Fact]
    public void EmitParser_RequireMethod_DoesNotEmbedPositionInMessage()
    {
        // The old pattern embedded position in the message string like:
        // "at line {cur.Start.Line}, column {cur.Start.Column}"
        // This should no longer appear in the message portion
        Assert.DoesNotContain("at line {cur.Start.Line}", output);
        Assert.DoesNotContain("at position", output);
    }

    [Fact]
    public void EmitParser_ErrorMethod_DoesNotEmbedPositionInMessage()
    {
        // The Error method should not embed position in the message string
        // The message should be just $"{message}" without appended position info
        var errorIndex = output.IndexOf("private static void Error(string message)");
        Assert.True(errorIndex >= 0, "Error method not found");

        var errorBody = output.Substring(errorIndex, 400);
        // Should not have old-style single-arg Add with embedded position
        Assert.DoesNotContain("$\"{message} at", errorBody);
    }

    [Fact]
    public void EmitParser_DoesNotUseSingleArgDiagnosticsAddWithPosition()
    {
        // There should be no single-argument diagnostics.Add call that
        // embeds position data in an interpolated string
        Assert.DoesNotContain(
            "_diagnostics?.Add($\"Expected token kind {kind} but found '{cur.Lexeme}' at",
            output);
    }
}