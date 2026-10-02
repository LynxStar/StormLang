using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Unit and property-based tests for the <c>inline</c> keyword and <c>,</c> comma
/// token classification in the Storm lexer.
/// Validates: Requirements 1.1, 1.2, 1.3, 1.4, 11.1, 11.2, 11.3
/// </summary>
public class InlineKeywordLexerTests
{
    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static List<StormToken> Tokenize(string input, out List<StormDiagnostic> diagnostics)
    {
        diagnostics = new List<StormDiagnostic>();
        var source = new StormSourceText("test.storm", input);
        return StormLexer.Tokenize(source, diagnostics);
    }

    private static List<StormToken> Tokenize(string input)
    {
        return Tokenize(input, out _);
    }

    private static List<StormToken> ContentTokens(string input)
    {
        return Tokenize(input).Where(t => t.Kind != StormTokenKind.EndOfFile).ToList();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: inline keyword classification
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Tokenize_InlineKeyword_Standalone()
    {
        var tokens = ContentTokens("inline");
        _ = Assert.Single(tokens);
        Assert.Equal(StormTokenKind.InlineKeyword, tokens[0].Kind);
        Assert.Equal("inline", tokens[0].Lexeme);
    }

    [Fact]
    public void Tokenize_InlineKeyword_FollowedByWhitespace()
    {
        var tokens = ContentTokens("inline ModifiedTypeHead");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(StormTokenKind.InlineKeyword, tokens[0].Kind);
        Assert.Equal("inline", tokens[0].Lexeme);
        Assert.Equal(StormTokenKind.Identifier, tokens[1].Kind);
    }

    [Fact]
    public void Tokenize_InlineKeyword_AtStartOfInput()
    {
        var tokens = ContentTokens("inline:");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(StormTokenKind.InlineKeyword, tokens[0].Kind);
        Assert.Equal(StormTokenKind.Colon, tokens[1].Kind);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: identifiers containing "inline" as substring
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("inlineFoo")]
    [InlineData("myinline")]
    [InlineData("_inline")]
    [InlineData("inline_")]
    [InlineData("inlineRule")]
    [InlineData("notinline")]
    public void Tokenize_IdentifierContainingInline_ProducesIdentifier(string identifier)
    {
        var tokens = ContentTokens(identifier);
        _ = Assert.Single(tokens);
        Assert.Equal(StormTokenKind.Identifier, tokens[0].Kind);
        Assert.Equal(identifier, tokens[0].Lexeme);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: comma classification
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Tokenize_Comma_Standalone()
    {
        var tokens = ContentTokens(",");
        _ = Assert.Single(tokens);
        Assert.Equal(StormTokenKind.Comma, tokens[0].Kind);
        Assert.Equal(",", tokens[0].Lexeme);
    }

    [Fact]
    public void Tokenize_Comma_CorrectOffset()
    {
        // "ab," => tokens: Identifier("ab") at offset 0, Comma(",") at offset 2
        var tokens = ContentTokens("ab,");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(StormTokenKind.Comma, tokens[1].Kind);
        Assert.Equal(2, tokens[1].Offset);
        Assert.Equal(1, tokens[1].Line);
        Assert.Equal(3, tokens[1].Column);
    }

    [Fact]
    public void Tokenize_Comma_OnSecondLine()
    {
        var tokens = ContentTokens("Foo\n,");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(StormTokenKind.Comma, tokens[1].Kind);
        Assert.Equal(4, tokens[1].Offset);
        Assert.Equal(2, tokens[1].Line);
        Assert.Equal(1, tokens[1].Column);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: inline inside string literals / regex literals
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Tokenize_InlineInsideStringLiteral_NotClassifiedAsKeyword()
    {
        var tokens = ContentTokens("\"inline\"");
        _ = Assert.Single(tokens);
        Assert.Equal(StormTokenKind.StringLiteral, tokens[0].Kind);
        Assert.Equal("\"inline\"", tokens[0].Lexeme);
    }

    [Fact]
    public void Tokenize_InlineInsideRegexLiteral_NotClassifiedAsKeyword()
    {
        var tokens = ContentTokens("`inline`");
        _ = Assert.Single(tokens);
        Assert.Equal(StormTokenKind.RegexLiteral, tokens[0].Kind);
        Assert.Equal("`inline`", tokens[0].Lexeme);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Property 1: Keyword Classification — inline Produces InlineKeyword
    //  Feature: inline-rule-prefix, Property 1: Keyword Classification
    //
    //  For any whitespace prefix and non-identifier suffix,
    //  "inline" surrounded by non-identifier chars produces InlineKeyword.
    //  **Validates: Requirements 1.1, 1.3**
    // ═══════════════════════════════════════════════════════════════════

    private static Gen<string> GenWhitespace()
    {
        return Gen.Elements(' ', '\t', '\n', '\r')
            .ArrayOf()
            .Select(chars => new string(chars));
    }

    private static Gen<string> GenNonIdentSuffix()
    {
        // Characters that are not identifier-continuation chars
        return Gen.Elements(":", " ", ";", "|", "{", "}", "(", ")", "\n", "");
    }

    [Property(MaxTest = 100)]
    public Property P1_InlineKeyword_StandaloneInline_ProducesInlineKeyword()
    {
        var whitespaceGen = GenWhitespace();
        var suffixGen = GenNonIdentSuffix();

        var inputGen = whitespaceGen.SelectMany(ws =>
            suffixGen.Select(suffix => ws + "inline" + suffix));

        return Prop.ForAll(inputGen.ToArbitrary(), input =>
        {
            var tokens = ContentTokens(input);

            var inlineTokens = tokens.Where(t => t.Kind == StormTokenKind.InlineKeyword).ToList();
            _ = Assert.Single(inlineTokens);
            Assert.Equal("inline", inlineTokens[0].Lexeme);

            // No token should be an Identifier with lexeme "inline"
            Assert.DoesNotContain(tokens,
                t => t.Kind == StormTokenKind.Identifier && t.Lexeme == "inline");
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Property 2: Non-Standalone inline Remains Identifier
    //  Feature: inline-rule-prefix, Property 2: Non-Standalone inline Remains Identifier
    //
    //  For any identifier that contains "inline" as a proper substring,
    //  the lexer produces a single Identifier token for the whole string.
    //  **Validates: Requirements 1.2**
    // ═══════════════════════════════════════════════════════════════════

    private static Gen<string> GenIdentifierContainingInline()
    {
        var identStartChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_".ToCharArray();
        var identPartChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_".ToCharArray();

        // Generate a prefix (1+ ident chars) or suffix (1+ ident chars) around "inline"
        var prefixGen = Gen.Choose(0, 8).SelectMany(len =>
            Gen.ArrayOf(Gen.Elements(identPartChars), len)
                .Select(chars => new string(chars)));

        var suffixGen = Gen.Choose(0, 8).SelectMany(len =>
            Gen.ArrayOf(Gen.Elements(identPartChars), len)
                .Select(chars => new string(chars)));

        // At least one of prefix/suffix must be non-empty to make it a proper superset of "inline"
        return prefixGen.SelectMany(prefix =>
            suffixGen.Select(suffix => (prefix, suffix)))
            .Where(pair => pair.prefix.Length > 0 || pair.suffix.Length > 0)
            .Select(pair =>
            {
                var combined = pair.prefix + "inline" + pair.suffix;
                // Ensure it starts with an ident-start char
                if(combined.Length > 0 && combined[0] >= '0' && combined[0] <= '9')
                {
                    combined = "_" + combined;
                }

                return combined;
            });
    }

    [Property(MaxTest = 100)]
    public Property P2_NonStandaloneInline_ProducesIdentifier()
    {
        return Prop.ForAll(GenIdentifierContainingInline().ToArbitrary(), identifier =>
        {
            var tokens = ContentTokens(identifier);

            _ = Assert.Single(tokens);
            Assert.Equal(StormTokenKind.Identifier, tokens[0].Kind);
            Assert.Equal(identifier, tokens[0].Lexeme);

            // Must NOT produce InlineKeyword
            Assert.DoesNotContain(tokens, t => t.Kind == StormTokenKind.InlineKeyword);
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Property 3: Comma Classification
    //  Feature: inline-rule-prefix, Property 3: Comma Classification
    //
    //  For any source text containing a comma outside string/regex/charclass
    //  literals, the lexer produces a Comma token at the correct offset.
    //  **Validates: Requirements 1.4, 11.2, 11.3**
    // ═══════════════════════════════════════════════════════════════════

    private static Gen<string> GenNonCommaIdentifier()
    {
        var identStartChars = "abcdefghijklmnopqrstuvwxyz".ToCharArray();
        var identPartChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

        return Gen.Elements(identStartChars).SelectMany(start =>
            Gen.Choose(0, 5).SelectMany(len =>
                Gen.ArrayOf(Gen.Elements(identPartChars), len)
                    .Select(chars => start + new string(chars))));
    }

    [Property(MaxTest = 100)]
    public Property P3_CommaClassification_ProducesCommaAtCorrectOffset()
    {
        // Generate inputs of the form: identifier + "," with known offset
        var identGen = GenNonCommaIdentifier();

        return Prop.ForAll(identGen.ToArbitrary(), ident =>
        {
            var input = ident + ",";
            var expectedOffset = ident.Length;

            var tokens = ContentTokens(input);
            Assert.Equal(2, tokens.Count);

            var commaToken = tokens[1];
            Assert.Equal(StormTokenKind.Comma, commaToken.Kind);
            Assert.Equal(",", commaToken.Lexeme);
            Assert.Equal(expectedOffset, commaToken.Offset);
            Assert.Equal(1, commaToken.Line);
            Assert.Equal(expectedOffset + 1, commaToken.Column);
        });
    }
}