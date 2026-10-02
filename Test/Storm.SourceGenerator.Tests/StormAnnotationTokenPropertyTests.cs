using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Property-based tests for annotation token lexing.
/// Feature: parser-memoization-expansion, Property 1: Annotation token lexing
/// </summary>
public class StormAnnotationTokenPropertyTests
{
    // ══════════════════════════════════════════════════════════════════
    //  Generator: Valid Identifiers
    //  Produces strings matching [a-zA-Z_][a-zA-Z0-9_]*
    // ══════════════════════════════════════════════════════════════════

    private static Gen<string> GenValidIdentifier()
    {
        var identStartChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_"
            .ToCharArray();

        var identPartChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_"
            .ToCharArray();

        var startGen = Gen.Elements(identStartChars);

        var tailGen = Gen.Choose(0, 15).SelectMany(len =>
            Gen.ArrayOf(Gen.Elements(identPartChars), len)
                .Select(chars => new string(chars)));

        return startGen.SelectMany(start =>
            tailGen.Select(tail => start + tail));
    }

    // ══════════════════════════════════════════════════════════════════
    //  1.3 — Property 1: Annotation token lexing
    //  For any string of form `@` + valid identifier, lexer produces
    //  exactly one `Annotation` token with lexeme equal to full input.
    //  **Validates: Requirements 1.1, 1.3**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 200)]
    public Property P1_AnnotationTokenLexing_AtPlusValidIdentifier_ProducesSingleAnnotationToken()
    {
        return Prop.ForAll(GenValidIdentifier().ToArbitrary(), identifier =>
        {
            var input = "@" + identifier;
            var diagnostics = new List<StormDiagnostic>();
            var source = new StormSourceText("test.storm", input);
            var tokens = StormLexer.Tokenize(source, diagnostics);

            // Filter out EndOfFile token
            var contentTokens = tokens.Where(t => t.Kind != StormTokenKind.EndOfFile).ToList();

            Assert.Empty(diagnostics);
            _ = Assert.Single(contentTokens);
            Assert.Equal(StormTokenKind.Annotation, contentTokens[0].Kind);
            Assert.Equal(input, contentTokens[0].Lexeme);
        });
    }
}