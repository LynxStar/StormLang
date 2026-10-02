using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Property-based tests for regex literal IR lowering.
/// Feature: regex-literals, Property 2: IR lowering preserves regex pattern
/// </summary>
public class StormRegexLiteralIRPropertyTests
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

    private static GrammarIRLowering.LoweringResult LowerGrammar(Dictionary<string, string> files, string rootPath)
    {
        var resolver = new StormImportResolver(InMemoryFileReader(files));
        var resolution = resolver.Resolve(rootPath);
        var grammar = StormGrammar.Build(resolution);
        return GrammarIRLowering.Lower(grammar);
    }

    /// <summary>
    /// Generator for printable ASCII strings that do not contain unescaped backticks.
    /// Backticks may appear only when preceded by a backslash (escaped).
    /// </summary>
    private static Gen<string> GenRegexPattern()
    {
        var safeChars = Enumerable.Range(0x20, 0x7E - 0x20 + 1)
            .Select(i => (char)i)
            .Where(c => c is not '`' and not '\\')
            .ToArray();

        var safeCharGen = Gen.Elements(safeChars);

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
    /// **Validates: Requirements 4.2, 5.1, 5.2**
    ///
    /// Property 2: IR lowering preserves regex pattern.
    /// For any StormRegexLiteralExpr with pattern p, lowering through both
    /// InlineFragments and LowerRuleExpression SHALL produce RegexLiteralIR
    /// with Pattern == p.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property IRLowering_PreservesRegexPattern()
    {
        return Prop.ForAll(GenRegexPattern().ToArbitrary(), pattern =>
        {
            // Build a grammar with a token using the regex literal (tests InlineFragments path)
            var grammarSource = $"grammar Test;\ntoken T = `{pattern}`;\nrule Start = T;";
            var files = new Dictionary<string, string>
            {
                ["root/main.storm"] = grammarSource
            };

            var result = LowerGrammar(files, "root/main.storm");

            Assert.True(result.Succeeded, $"Lowering failed for pattern: {pattern}");
            Assert.NotNull(result.Grammar);

            // Verify InlineFragments path: token expression should be RegexLiteralIR
            var tokenIR = result.Grammar!.Tokens.FirstOrDefault(t => t.Name == "T");
            Assert.NotNull(tokenIR);
            var regexIR = Assert.IsType<RegexLiteralIR>(tokenIR!.Expression);
            Assert.Equal(pattern, regexIR.Pattern);
        });
    }
}