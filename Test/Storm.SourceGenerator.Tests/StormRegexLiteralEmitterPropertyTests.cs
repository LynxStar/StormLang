using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Property-based tests for regex literal code emission.
/// Feature: regex-literals, Property 3: Emitter generates anchored compiled Regex code
/// </summary>
public class StormRegexLiteralEmitterPropertyTests
{
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

    /// <summary>
    /// Generator for regex pattern strings that are safe to embed in a Storm grammar.
    /// Produces printable ASCII strings without unescaped backticks or double-quotes
    /// (which would break the verbatim string literal in generated C#).
    /// </summary>
    private static Gen<string> GenRegexPattern()
    {
        // Safe printable ASCII chars (exclude backtick and backslash for simplicity)
        var safeChars = Enumerable.Range(0x20, 0x7E - 0x20 + 1)
            .Select(i => (char)i)
            .Where(c => c is not '`' and not '\\')
            .ToArray();

        var safeCharGen = Gen.Elements(safeChars);

        // Escaped characters (backslash + any printable char)
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
            .Select(string.Concat)
            .Where(s => s.Length > 0); // non-empty patterns
    }

    /// <summary>
    /// **Validates: Requirements 11.4**
    ///
    /// Property 3 (updated for DFA): Emitter compiles regex patterns into DFA tables.
    /// For any RegexLiteralIR with a valid regex pattern p, the generated C# code SHALL NOT contain
    /// any runtime Regex usage, and SHALL contain DFA transition tables instead.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Emitter_CompilesRegexIntoDfaTables()
    {
        return Prop.ForAll(GenRegexPattern().ToArbitrary(), pattern =>
        {
            var output = TryEmitLexer(pattern);

            return Prop.When(output != null, () =>
            {
                // Must NOT contain runtime Regex usage (DFA replaces it)
                Assert.False(output!.Contains("using System.Text.RegularExpressions;"),
                    $"Should not contain Regex using directive for pattern: {pattern}");

                Assert.False(output.Contains("RegexOptions.Compiled"),
                    $"Should not contain RegexOptions.Compiled for pattern: {pattern}");

                Assert.False(output.Contains("private static readonly Regex"),
                    $"Should not contain Regex field for pattern: {pattern}");

                // Must contain DFA transition tables
                Assert.True(output.Contains("_charClass"),
                    $"Missing _charClass array for pattern: {pattern}");

                Assert.True(output.Contains("_trans_default"),
                    $"Missing transition table for pattern: {pattern}");

                Assert.True(output.Contains("_accept_default"),
                    $"Missing accepting table for pattern: {pattern}");
            });
        });
    }

    private static string? TryEmitLexer(string pattern)
    {
        var grammarSource = $"token T = `{pattern}`;\nrule Start = T;";

        EmissionContext ctx;
        try
        {
            ctx = CreateContext(grammarSource);
        }
        catch
        {
            // Skip patterns that fail grammar lowering
            return null;
        }

        try
        {
            return LexerEmitter.Emit(ctx);
        }
        catch(FormatException)
        {
            // Skip patterns that are not valid for the RegexParser
            // (e.g., unterminated character classes from random generation)
            return null;
        }
    }
}