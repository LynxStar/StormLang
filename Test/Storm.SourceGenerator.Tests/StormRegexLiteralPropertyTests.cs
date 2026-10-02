using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Property-based tests for regex literal lex-parse round trip.
/// Feature: regex-literals, Property 1: Lex-parse round trip preserves regex pattern
/// </summary>
public class StormRegexLiteralPropertyTests
{
    /// <summary>
    /// Generator for printable ASCII strings that do not contain unescaped backticks.
    /// Backticks may appear only when preceded by a backslash (escaped).
    /// A trailing lone backslash is avoided because it would escape the closing backtick delimiter.
    /// </summary>
    private static Gen<string> GenRegexPattern()
    {
        // Printable ASCII chars excluding backtick and backslash
        var safeChars = Enumerable.Range(0x20, 0x7E - 0x20 + 1)
            .Select(i => (char)i)
            .Where(c => c is not '`' and not '\\')
            .ToArray();

        var safeCharGen = Gen.Elements(safeChars);

        // Generate a list of "segments":
        // - a safe char (no backtick, no backslash)
        // - an escape sequence: backslash followed by a printable ASCII char (including backtick)
        var escapedChars = Enumerable.Range(0x20, 0x7E - 0x20 + 1)
            .Select(i => (char)i)
            .ToArray();

        var escapeGen = Gen.Elements(escapedChars)
            .Select(c => "\\" + c);

        var segmentGen = Gen.Frequency(
            (8, safeCharGen.Select(c => c.ToString())),
            (2, escapeGen)
        );

        return segmentGen.ListOf()
            .Select(string.Concat);
    }

    /// <summary>
    /// **Validates: Requirements 1.1, 1.5, 2.1, 3.2**
    ///
    /// Property 1: Lex-parse round trip preserves regex pattern.
    /// For any string p not containing an unescaped backtick, wrapping in
    /// token T = `p`;, tokenizing, and parsing SHALL produce
    /// StormRegexLiteralExpr with Pattern == p.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property LexParseRoundTrip_PreservesRegexPattern()
    {
        return Prop.ForAll(GenRegexPattern().ToArbitrary(), pattern =>
        {
            var input = $"token T = `{pattern}`;";
            var diagnostics = new List<StormDiagnostic>();
            var source = new StormSourceText("test.storm", input);
            var tokens = StormLexer.Tokenize(source, diagnostics);
            var file = StormParser.Parse(tokens, source, diagnostics);

            Assert.Empty(diagnostics);
            _ = Assert.Single(file.Declarations);
            var decl = Assert.IsType<StormTokenDecl>(file.Declarations[0]);
            var regexExpr = Assert.IsType<StormRegexLiteralExpr>(decl.Body);
            Assert.Equal(pattern, regexExpr.Pattern);
        });
    }
}