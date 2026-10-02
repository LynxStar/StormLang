using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Property-based tests for Storm frontend diagnostic location completeness.
/// Feature: unified-diagnostic-formatting, Property 6: Storm frontend diagnostic location completeness
/// </summary>
public class StormFrontendDiagnosticLocationPropertyTests
{
    // ══════════════════════════════════════════════════════════════════
    //  Generators
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Generates non-null, non-empty file paths for Storm source texts.
    /// </summary>
    private static Gen<string> GenFilePath()
    {
        var segments = new[] { "src", "lib", "grammar", "test", "main", "utils" };
        var extensions = new[] { ".storm", ".grammar", ".txt" };

        return Gen.Elements(segments)
            .SelectMany(dir => Gen.Elements(segments)
                .SelectMany(file => Gen.Elements(extensions)
                    .Select(ext => $"{dir}/{file}{ext}")));
    }

    /// <summary>
    /// Generates random invalid Storm grammar source texts that will trigger
    /// lexer and/or parser errors. These include:
    /// - Strings with unrecognized characters (e.g., @, #, $, ~, etc.)
    /// - Unterminated string literals
    /// - Unterminated character classes
    /// - Unterminated regex literals
    /// - Incomplete/malformed grammar declarations
    /// - Random garbage text
    /// </summary>
    private static Gen<string> GenInvalidStormSource()
    {
        return Gen.Frequency(
            // Unrecognized characters
            (2, GenUnrecognizedCharSource()),
            // Unterminated string literal
            (1, GenUnterminatedStringSource()),
            // Unterminated character class
            (1, GenUnterminatedCharClassSource()),
            // Unterminated regex literal
            (1, GenUnterminatedRegexSource()),
            // Incomplete grammar declarations (parser errors)
            (2, GenIncompleteDeclarationSource()),
            // Random garbage that triggers both lexer and parser errors
            (2, GenGarbageSource())
        );
    }

    private static Gen<string> GenUnrecognizedCharSource()
    {
        var badChars = new[] { '@', '#', '$', '~', '&', '%', '^', '\\', '!' };
        return Gen.Choose(1, 5).SelectMany(count =>
            Gen.ArrayOf(Gen.Elements(badChars), count)
                .Select(chars => new string(chars)));
    }

    private static Gen<string> GenUnterminatedStringSource()
    {
        return Gen.Elements(
            "\"unterminated",
            "token X = \"hello",
            "grammar Test;\ntoken Y = \"oops"
        );
    }

    private static Gen<string> GenUnterminatedCharClassSource()
    {
        return Gen.Elements(
            "[abc",
            "token X = [a-z",
            "grammar Test;\ntoken Y = [^0-9"
        );
    }

    private static Gen<string> GenUnterminatedRegexSource()
    {
        return Gen.Elements(
            "`abc",
            "token X = `[0-9]+",
            "grammar Test;\ntoken Y = `foo"
        );
    }

    private static Gen<string> GenIncompleteDeclarationSource()
    {
        return Gen.Elements(
            // Missing semicolons, missing names, missing bodies
            "grammar",
            "grammar ;",
            "rule",
            "rule ;",
            "rule Foo",
            "rule Foo =",
            "rule Foo = ;",
            "token",
            "token Bar =",
            "grammar Test;\nrule",
            "grammar Test;\ntoken Foo = ;\nrule",
            "grammar Test;\nhidden",
            "grammar Test;\nfragment",
            // Unexpected tokens in declaration position
            "grammar Test;\n123",
            "grammar Test;\n@@@"
        );
    }

    private static Gen<string> GenGarbageSource()
    {
        // Mix of valid-looking prefixes with invalid continuations
        var garbageChars = "abcdefghijklmnopqrstuvwxyz0123456789@#$%^&!~\"[`\n \t;=|"
            .ToCharArray();

        return Gen.Choose(1, 30).SelectMany(len =>
            Gen.ArrayOf(Gen.Elements(garbageChars), len)
                .Select(chars => new string(chars)));
    }

    // ══════════════════════════════════════════════════════════════════
    //  Property 6: Storm frontend diagnostic location completeness
    //
    //  For any invalid Storm grammar source text with a non-null file
    //  path, all StormDiagnostic records produced by the StormLexer or
    //  StormParser SHALL have a non-null FilePath and Line > 0 and
    //  Column > 0.
    //
    //  **Validates: Requirements 6.3**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 100)]
    public Property P6_StormLexer_AllDiagnosticsHaveValidLocation()
    {
        var gen = GenFilePath().SelectMany(filePath =>
            GenInvalidStormSource().Select(source => (filePath, source)));

        return Prop.ForAll(gen.ToArbitrary(), pair =>
        {
            var (filePath, sourceText) = pair;
            var source = new StormSourceText(filePath, sourceText);
            var diagnostics = new List<StormDiagnostic>();

            _ = StormLexer.Tokenize(source, diagnostics);

            foreach(var diag in diagnostics)
            {
                Assert.NotNull(diag.FilePath);
                Assert.True(diag.Line > 0,
                    $"Expected Line > 0 but got {diag.Line} for diagnostic '{diag.Message}' in source: {sourceText}");
                Assert.True(diag.Column > 0,
                    $"Expected Column > 0 but got {diag.Column} for diagnostic '{diag.Message}' in source: {sourceText}");
            }
        });
    }

    [Property(MaxTest = 100)]
    public Property P6_StormParser_AllDiagnosticsHaveValidLocation()
    {
        var gen = GenFilePath().SelectMany(filePath =>
            GenInvalidStormSource().Select(source => (filePath, source)));

        return Prop.ForAll(gen.ToArbitrary(), pair =>
        {
            var (filePath, sourceText) = pair;
            var source = new StormSourceText(filePath, sourceText);
            var lexerDiagnostics = new List<StormDiagnostic>();

            var tokens = StormLexer.Tokenize(source, lexerDiagnostics);

            var parserDiagnostics = new List<StormDiagnostic>();
            _ = StormParser.Parse(tokens, source, parserDiagnostics);

            // Verify all parser diagnostics have valid location
            foreach(var diag in parserDiagnostics)
            {
                Assert.NotNull(diag.FilePath);
                Assert.True(diag.Line > 0,
                    $"Expected Line > 0 but got {diag.Line} for diagnostic '{diag.Message}' in source: {sourceText}");
                Assert.True(diag.Column > 0,
                    $"Expected Column > 0 but got {diag.Column} for diagnostic '{diag.Message}' in source: {sourceText}");
            }
        });
    }

    [Property(MaxTest = 100)]
    public Property P6_StormFrontend_CombinedDiagnosticsHaveValidLocation()
    {
        var gen = GenFilePath().SelectMany(filePath =>
            GenInvalidStormSource().Select(source => (filePath, source)));

        return Prop.ForAll(gen.ToArbitrary(), pair =>
        {
            var (filePath, sourceText) = pair;
            var source = new StormSourceText(filePath, sourceText);
            var diagnostics = new List<StormDiagnostic>();

            // Run through both lexer and parser, collecting all diagnostics
            var tokens = StormLexer.Tokenize(source, diagnostics);
            _ = StormParser.Parse(tokens, source, diagnostics);

            // At least some diagnostics should be produced for invalid input
            // (not strictly required by the property, but validates our generators)

            // The core property: every diagnostic has valid location data
            foreach(var diag in diagnostics)
            {
                Assert.NotNull(diag.FilePath);
                Assert.True(diag.Line > 0,
                    $"Expected Line > 0 but got {diag.Line} for diagnostic '{diag.Message}' in source: {sourceText}");
                Assert.True(diag.Column > 0,
                    $"Expected Column > 0 but got {diag.Column} for diagnostic '{diag.Message}' in source: {sourceText}");
            }
        });
    }
}