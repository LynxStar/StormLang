using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;

namespace Storm.SourceGenerator.Tests;

public class StormLexerTests
{
    // ---------------------------------------------------------------
    //  Helpers
    // ---------------------------------------------------------------

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

    /// <summary>Returns tokens excluding the trailing EndOfFile.</summary>
    private static List<StormToken> ContentTokens(string input)
    {
        return Tokenize(input).Where(t => t.Kind != StormTokenKind.EndOfFile).ToList();
    }

    // ---------------------------------------------------------------
    //  Keywords
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("rule", StormTokenKind.RuleKeyword)]
    [InlineData("token", StormTokenKind.TokenKeyword)]
    [InlineData("fragment", StormTokenKind.FragmentKeyword)]
    [InlineData("hidden", StormTokenKind.HiddenKeyword)]
    [InlineData("import", StormTokenKind.ImportKeyword)]
    [InlineData("grammar", StormTokenKind.GrammarKeyword)]
    [InlineData("as", StormTokenKind.AsKeyword)]
    [InlineData("trait", StormTokenKind.TraitKeyword)]
    public void Tokenize_Keywords(string keyword, StormTokenKind expectedKind)
    {
        var tokens = ContentTokens(keyword);
        _ = Assert.Single(tokens);
        Assert.Equal(expectedKind, tokens[0].Kind);
        Assert.Equal(keyword, tokens[0].Lexeme);
    }

    // ---------------------------------------------------------------
    //  Identifiers
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("Foo")]
    [InlineData("_bar")]
    [InlineData("baz123")]
    [InlineData("A")]
    public void Tokenize_Identifiers(string ident)
    {
        var tokens = ContentTokens(ident);
        _ = Assert.Single(tokens);
        Assert.Equal(StormTokenKind.Identifier, tokens[0].Kind);
        Assert.Equal(ident, tokens[0].Lexeme);
    }

    [Fact]
    public void Tokenize_IdentifierNotKeyword()
    {
        // "rules" is not the keyword "rule"
        var tokens = ContentTokens("rules");
        _ = Assert.Single(tokens);
        Assert.Equal(StormTokenKind.Identifier, tokens[0].Kind);
        Assert.Equal("rules", tokens[0].Lexeme);
    }

    [Fact]
    public void Tokenize_TraitKeyword_WithSuffix_IsIdentifier()
    {
        // "traits" starts with "trait" but has additional characters
        var tokens = ContentTokens("traits");
        _ = Assert.Single(tokens);
        Assert.Equal(StormTokenKind.Identifier, tokens[0].Kind);
        Assert.Equal("traits", tokens[0].Lexeme);
    }

    [Fact]
    public void Tokenize_TraitKeyword_CaseMismatch_IsIdentifier()
    {
        // "Trait" differs from "trait" only by casing
        var tokens = ContentTokens("Trait");
        _ = Assert.Single(tokens);
        Assert.Equal(StormTokenKind.Identifier, tokens[0].Kind);
        Assert.Equal("Trait", tokens[0].Lexeme);
    }

    // ---------------------------------------------------------------
    //  String literals
    // ---------------------------------------------------------------

    [Fact]
    public void Tokenize_StringLiteral_Simple()
    {
        var tokens = ContentTokens("\"hello\"");
        _ = Assert.Single(tokens);
        Assert.Equal(StormTokenKind.StringLiteral, tokens[0].Kind);
        Assert.Equal("\"hello\"", tokens[0].Lexeme);
    }

    [Fact]
    public void Tokenize_StringLiteral_WithEscape()
    {
        var tokens = ContentTokens("\"he\\\"llo\"");
        _ = Assert.Single(tokens);
        Assert.Equal(StormTokenKind.StringLiteral, tokens[0].Kind);
        Assert.Equal("\"he\\\"llo\"", tokens[0].Lexeme);
    }

    [Fact]
    public void Tokenize_StringLiteral_Unterminated()
    {
        var tokens = Tokenize("\"hello", out var diags);
        Assert.Contains(tokens, t => t.Kind == StormTokenKind.StringLiteral);
        _ = Assert.Single(diags);
        Assert.Contains("Unterminated string literal", diags[0].Message);
    }

    // ---------------------------------------------------------------
    //  Character classes
    // ---------------------------------------------------------------

    [Fact]
    public void Tokenize_CharClass_Simple()
    {
        var tokens = ContentTokens("[a-z]");
        _ = Assert.Single(tokens);
        Assert.Equal(StormTokenKind.CharClass, tokens[0].Kind);
        Assert.Equal("[a-z]", tokens[0].Lexeme);
    }

    [Fact]
    public void Tokenize_CharClass_Negated()
    {
        var tokens = ContentTokens("[^\"\\\\]");
        _ = Assert.Single(tokens);
        Assert.Equal(StormTokenKind.CharClass, tokens[0].Kind);
    }

    [Fact]
    public void Tokenize_CharClass_Unterminated()
    {
        var tokens = Tokenize("[abc", out var diags);
        Assert.Contains(tokens, t => t.Kind == StormTokenKind.CharClass);
        _ = Assert.Single(diags);
        Assert.Contains("Unterminated character class", diags[0].Message);
    }

    // ---------------------------------------------------------------
    //  Operators / punctuation
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("=", StormTokenKind.Equals)]
    [InlineData("|", StormTokenKind.Pipe)]
    [InlineData(";", StormTokenKind.Semicolon)]
    [InlineData("(", StormTokenKind.LeftParen)]
    [InlineData(")", StormTokenKind.RightParen)]
    [InlineData("?", StormTokenKind.Question)]
    [InlineData("*", StormTokenKind.Star)]
    [InlineData("+", StormTokenKind.Plus)]
    [InlineData(":", StormTokenKind.Colon)]
    [InlineData(".", StormTokenKind.Dot)]
    public void Tokenize_Operators(string op, StormTokenKind expectedKind)
    {
        var tokens = ContentTokens(op);
        _ = Assert.Single(tokens);
        Assert.Equal(expectedKind, tokens[0].Kind);
        Assert.Equal(op, tokens[0].Lexeme);
    }

    // ---------------------------------------------------------------
    //  Comments
    // ---------------------------------------------------------------

    [Fact]
    public void Tokenize_LineComment_Skipped()
    {
        var tokens = ContentTokens("// this is a comment");
        Assert.Empty(tokens);
    }

    [Fact]
    public void Tokenize_LineComment_BeforeToken()
    {
        var tokens = ContentTokens("// comment\nrule");
        _ = Assert.Single(tokens);
        Assert.Equal(StormTokenKind.RuleKeyword, tokens[0].Kind);
    }

    [Fact]
    public void Tokenize_LineComment_AfterToken()
    {
        var tokens = ContentTokens("rule // comment");
        _ = Assert.Single(tokens);
        Assert.Equal(StormTokenKind.RuleKeyword, tokens[0].Kind);
    }

    // ---------------------------------------------------------------
    //  Whitespace
    // ---------------------------------------------------------------

    [Fact]
    public void Tokenize_WhitespaceSkipped()
    {
        var tokens = ContentTokens("  rule  Foo  ");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(StormTokenKind.RuleKeyword, tokens[0].Kind);
        Assert.Equal(StormTokenKind.Identifier, tokens[1].Kind);
    }

    // ---------------------------------------------------------------
    //  EndOfFile
    // ---------------------------------------------------------------

    [Fact]
    public void Tokenize_EmptyInput_ProducesEOF()
    {
        var tokens = Tokenize("");
        _ = Assert.Single(tokens);
        Assert.Equal(StormTokenKind.EndOfFile, tokens[0].Kind);
    }

    [Fact]
    public void Tokenize_AlwaysEndsWithEOF()
    {
        var tokens = Tokenize("rule Foo = Bar;");
        Assert.Equal(StormTokenKind.EndOfFile, tokens.Last().Kind);
    }

    // ---------------------------------------------------------------
    //  Annotation tokens
    // ---------------------------------------------------------------

    [Fact]
    public void Tokenize_Annotation_Memo()
    {
        var tokens = ContentTokens("@memo");
        _ = Assert.Single(tokens);
        Assert.Equal(StormTokenKind.Annotation, tokens[0].Kind);
        Assert.Equal("@memo", tokens[0].Lexeme);
    }

    [Fact]
    public void Tokenize_Annotation_Unknown()
    {
        var tokens = ContentTokens("@unknown");
        _ = Assert.Single(tokens);
        Assert.Equal(StormTokenKind.Annotation, tokens[0].Kind);
        Assert.Equal("@unknown", tokens[0].Lexeme);
    }

    [Fact]
    public void Tokenize_Annotation_AtAlone_ProducesErrorToken()
    {
        var tokens = Tokenize("@", out var diags);
        Assert.Contains(tokens, t => t.Kind == StormTokenKind.Error);
        _ = Assert.Single(diags);
        Assert.Contains("Unrecognized character", diags[0].Message);
    }

    [Fact]
    public void Tokenize_Annotation_AtDigit_ProducesErrorToken()
    {
        var tokens = Tokenize("@123", out var diags);
        // '@' not followed by identifier-start produces an Error token for '@'
        Assert.Contains(tokens, t => t.Kind == StormTokenKind.Error && t.Lexeme == "@");
        // No Annotation token should be produced
        Assert.DoesNotContain(tokens, t => t.Kind == StormTokenKind.Annotation);
        // The first diagnostic should be about the '@' character
        Assert.Contains(diags, d => d.Message.Contains("Unrecognized character '@'"));
    }

    [Fact]
    public void Tokenize_Annotation_DoesNotInterfereWithExistingTokens()
    {
        // Annotation followed by other tokens should not disturb classification
        var tokens = ContentTokens("@memo rule Foo = Bar;");
        Assert.Equal(6, tokens.Count);
        Assert.Equal(StormTokenKind.Annotation, tokens[0].Kind);
        Assert.Equal("@memo", tokens[0].Lexeme);
        Assert.Equal(StormTokenKind.RuleKeyword, tokens[1].Kind);
        Assert.Equal(StormTokenKind.Identifier, tokens[2].Kind);
        Assert.Equal(StormTokenKind.Equals, tokens[3].Kind);
        Assert.Equal(StormTokenKind.Identifier, tokens[4].Kind);
        Assert.Equal(StormTokenKind.Semicolon, tokens[5].Kind);
    }

    // ---------------------------------------------------------------
    //  Error tokens
    // ---------------------------------------------------------------

    [Fact]
    public void Tokenize_UnrecognizedCharacter_ProducesErrorToken()
    {
        // Non-@ unrecognized characters (@ is now handled by annotation logic)
        var tokens = Tokenize("#", out var diags);
        Assert.Contains(tokens, t => t.Kind == StormTokenKind.Error);
        _ = Assert.Single(diags);
        Assert.Contains("Unrecognized character", diags[0].Message);
    }

    // ---------------------------------------------------------------
    //  Line / column tracking
    // ---------------------------------------------------------------

    [Fact]
    public void Tokenize_LineAndColumn_Correct()
    {
        var tokens = ContentTokens("rule\nFoo");
        Assert.Equal(2, tokens.Count);
        // "rule" at line 1, col 1
        Assert.Equal(1, tokens[0].Line);
        Assert.Equal(1, tokens[0].Column);
        // "Foo" at line 2, col 1
        Assert.Equal(2, tokens[1].Line);
        Assert.Equal(1, tokens[1].Column);
    }

    // ---------------------------------------------------------------
    //  Regex literals
    // ---------------------------------------------------------------

    [Fact]
    public void Tokenize_RegexLiteral_Simple()
    {
        var tokens = ContentTokens("`abc`");
        _ = Assert.Single(tokens);
        Assert.Equal(StormTokenKind.RegexLiteral, tokens[0].Kind);
        Assert.Equal("`abc`", tokens[0].Lexeme);
    }

    [Fact]
    public void Tokenize_RegexLiteral_EscapedBacktick()
    {
        var tokens = ContentTokens(@"`a\`b`");
        _ = Assert.Single(tokens);
        Assert.Equal(StormTokenKind.RegexLiteral, tokens[0].Kind);
        Assert.Equal(@"`a\`b`", tokens[0].Lexeme);
    }

    [Fact]
    public void Tokenize_RegexLiteral_Unterminated()
    {
        var tokens = Tokenize("`abc", out var diags);
        Assert.Contains(tokens, t => t.Kind == StormTokenKind.RegexLiteral);
        _ = Assert.Single(diags);
        Assert.Contains("Unterminated regex literal", diags[0].Message);
    }

    [Fact]
    public void Tokenize_RegexLiteral_Empty()
    {
        var tokens = ContentTokens("``");
        _ = Assert.Single(tokens);
        Assert.Equal(StormTokenKind.RegexLiteral, tokens[0].Kind);
        Assert.Equal("``", tokens[0].Lexeme);
    }

    // ---------------------------------------------------------------
    //  Full declaration tokenization
    // ---------------------------------------------------------------

    [Fact]
    public void Tokenize_FullDeclaration()
    {
        var tokens = ContentTokens("token Ident = [a-zA-Z_] [a-zA-Z0-9_]*;");
        var kinds = tokens.Select(t => t.Kind).ToList();
        Assert.Equal(new[]
        {
            StormTokenKind.TokenKeyword,
            StormTokenKind.Identifier,
            StormTokenKind.Equals,
            StormTokenKind.CharClass,
            StormTokenKind.CharClass,
            StormTokenKind.Star,
            StormTokenKind.Semicolon,
        }, kinds);
    }
}