using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Tests;

public class RegexParserTests
{
    // ── Simple literals ──────────────────────────────────────────────

    [Fact]
    public void Parse_SingleChar_ReturnsStringLiteral()
    {
        var result = RegexParser.Parse("a");
        var literal = Assert.IsType<StringLiteralIR>(result);
        Assert.Equal("a", literal.Value);
    }

    [Fact]
    public void Parse_MultipleChars_ReturnsSequence()
    {
        var result = RegexParser.Parse("abc");
        var seq = Assert.IsType<SequenceIR>(result);
        Assert.Equal(3, seq.Elements.Count);
        Assert.Equal("a", Assert.IsType<StringLiteralIR>(seq.Elements[0]).Value);
        Assert.Equal("b", Assert.IsType<StringLiteralIR>(seq.Elements[1]).Value);
        Assert.Equal("c", Assert.IsType<StringLiteralIR>(seq.Elements[2]).Value);
    }

    // ── Character classes ────────────────────────────────────────────

    [Fact]
    public void Parse_SimpleCharClass_ReturnsCharClassIR()
    {
        var result = RegexParser.Parse("[a-z]");
        var cc = Assert.IsType<CharClassIR>(result);
        Assert.Equal("[a-z]", cc.Text);
    }

    [Fact]
    public void Parse_NegatedCharClass_ReturnsCharClassIR()
    {
        var result = RegexParser.Parse("[^abc]");
        var cc = Assert.IsType<CharClassIR>(result);
        Assert.Equal("[^abc]", cc.Text);
    }

    [Fact]
    public void Parse_ComplexCharClass_ReturnsCharClassIR()
    {
        var result = RegexParser.Parse("[a-zA-Z0-9_]");
        var cc = Assert.IsType<CharClassIR>(result);
        Assert.Equal("[a-zA-Z0-9_]", cc.Text);
    }

    [Fact]
    public void Parse_CharClassWithEscapes_ReturnsCharClassIR()
    {
        var result = RegexParser.Parse("[\\n\\t]");
        var cc = Assert.IsType<CharClassIR>(result);
        Assert.Equal("[\\n\\t]", cc.Text);
    }

    // ── Escape sequences ─────────────────────────────────────────────

    [Fact]
    public void Parse_EscapeS_ReturnsWhitespaceCharClass()
    {
        var result = RegexParser.Parse("\\s");
        var cc = Assert.IsType<CharClassIR>(result);
        Assert.Equal("[ \\t\\n\\r\\f\\v]", cc.Text);
    }

    [Fact]
    public void Parse_EscapeD_ReturnsDigitCharClass()
    {
        var result = RegexParser.Parse("\\d");
        var cc = Assert.IsType<CharClassIR>(result);
        Assert.Equal("[0-9]", cc.Text);
    }

    [Fact]
    public void Parse_EscapeW_ReturnsWordCharClass()
    {
        var result = RegexParser.Parse("\\w");
        var cc = Assert.IsType<CharClassIR>(result);
        Assert.Equal("[a-zA-Z0-9_]", cc.Text);
    }

    [Fact]
    public void Parse_EscapeN_ReturnsNewlineLiteral()
    {
        var result = RegexParser.Parse("\\n");
        var literal = Assert.IsType<StringLiteralIR>(result);
        Assert.Equal("\n", literal.Value);
    }

    [Fact]
    public void Parse_EscapeT_ReturnsTabLiteral()
    {
        var result = RegexParser.Parse("\\t");
        var literal = Assert.IsType<StringLiteralIR>(result);
        Assert.Equal("\t", literal.Value);
    }

    [Fact]
    public void Parse_EscapeR_ReturnsCarriageReturnLiteral()
    {
        var result = RegexParser.Parse("\\r");
        var literal = Assert.IsType<StringLiteralIR>(result);
        Assert.Equal("\r", literal.Value);
    }

    [Fact]
    public void Parse_EscapeZero_ReturnsNullLiteral()
    {
        var result = RegexParser.Parse("\\0");
        var literal = Assert.IsType<StringLiteralIR>(result);
        Assert.Equal("\0", literal.Value);
    }

    [Fact]
    public void Parse_EscapeBackslash_ReturnsBackslashLiteral()
    {
        var result = RegexParser.Parse("\\\\");
        var literal = Assert.IsType<StringLiteralIR>(result);
        Assert.Equal("\\", literal.Value);
    }

    [Fact]
    public void Parse_EscapedDot_ReturnsLiteralDot()
    {
        var result = RegexParser.Parse("\\.");
        var literal = Assert.IsType<StringLiteralIR>(result);
        Assert.Equal(".", literal.Value);
    }

    // ── Dot metacharacter ────────────────────────────────────────────

    [Fact]
    public void Parse_Dot_ReturnsCharClassMatchingAnyExceptNewline()
    {
        var result = RegexParser.Parse(".");
        var cc = Assert.IsType<CharClassIR>(result);
        Assert.Equal("[^\\n]", cc.Text);
    }

    // ── Quantifiers ──────────────────────────────────────────────────

    [Fact]
    public void Parse_Plus_ReturnsOneOrMore()
    {
        var result = RegexParser.Parse("a+");
        var oneOrMore = Assert.IsType<OneOrMoreIR>(result);
        var inner = Assert.IsType<StringLiteralIR>(oneOrMore.Inner);
        Assert.Equal("a", inner.Value);
    }

    [Fact]
    public void Parse_Star_ReturnsZeroOrMore()
    {
        var result = RegexParser.Parse("a*");
        var zeroOrMore = Assert.IsType<ZeroOrMoreIR>(result);
        var inner = Assert.IsType<StringLiteralIR>(zeroOrMore.Inner);
        Assert.Equal("a", inner.Value);
    }

    [Fact]
    public void Parse_Question_ReturnsOptional()
    {
        var result = RegexParser.Parse("a?");
        var optional = Assert.IsType<OptionalIR>(result);
        var inner = Assert.IsType<StringLiteralIR>(optional.Inner);
        Assert.Equal("a", inner.Value);
    }

    [Fact]
    public void Parse_CharClassWithQuantifier_WrapsCorrectly()
    {
        var result = RegexParser.Parse("[a-z]+");
        var oneOrMore = Assert.IsType<OneOrMoreIR>(result);
        var cc = Assert.IsType<CharClassIR>(oneOrMore.Inner);
        Assert.Equal("[a-z]", cc.Text);
    }

    // ── Grouping ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_SimpleGroup_ReturnsInnerExpression()
    {
        var result = RegexParser.Parse("(a)");
        var literal = Assert.IsType<StringLiteralIR>(result);
        Assert.Equal("a", literal.Value);
    }

    [Fact]
    public void Parse_GroupWithQuantifier_WrapsGroupResult()
    {
        var result = RegexParser.Parse("(ab)+");
        var oneOrMore = Assert.IsType<OneOrMoreIR>(result);
        var seq = Assert.IsType<SequenceIR>(oneOrMore.Inner);
        Assert.Equal(2, seq.Elements.Count);
    }

    [Fact]
    public void Parse_NestedGroups_ProducesCorrectTree()
    {
        var result = RegexParser.Parse("((a))");
        var literal = Assert.IsType<StringLiteralIR>(result);
        Assert.Equal("a", literal.Value);
    }

    // ── Alternation ──────────────────────────────────────────────────

    [Fact]
    public void Parse_SimpleAlternation_ReturnsAlternationIR()
    {
        var result = RegexParser.Parse("a|b");
        var alt = Assert.IsType<AlternationIR>(result);
        Assert.Equal(2, alt.Alternatives.Count);
        Assert.Equal("a", Assert.IsType<StringLiteralIR>(alt.Alternatives[0]).Value);
        Assert.Equal("b", Assert.IsType<StringLiteralIR>(alt.Alternatives[1]).Value);
    }

    [Fact]
    public void Parse_MultipleAlternation_ReturnsAllAlternatives()
    {
        var result = RegexParser.Parse("a|b|c");
        var alt = Assert.IsType<AlternationIR>(result);
        Assert.Equal(3, alt.Alternatives.Count);
    }

    [Fact]
    public void Parse_AlternationWithConcatenation_CorrectPrecedence()
    {
        // "ab|cd" should be (ab)|(cd), not a(b|c)d
        var result = RegexParser.Parse("ab|cd");
        var alt = Assert.IsType<AlternationIR>(result);
        Assert.Equal(2, alt.Alternatives.Count);

        var left = Assert.IsType<SequenceIR>(alt.Alternatives[0]);
        Assert.Equal(2, left.Elements.Count);

        var right = Assert.IsType<SequenceIR>(alt.Alternatives[1]);
        Assert.Equal(2, right.Elements.Count);
    }

    // ── Concatenation ────────────────────────────────────────────────

    [Fact]
    public void Parse_ConcatenationWithQuantifiers_CorrectPrecedence()
    {
        // "a+b*" should be (a+)(b*), not (a+b)*
        var result = RegexParser.Parse("a+b*");
        var seq = Assert.IsType<SequenceIR>(result);
        Assert.Equal(2, seq.Elements.Count);
        _ = Assert.IsType<OneOrMoreIR>(seq.Elements[0]);
        _ = Assert.IsType<ZeroOrMoreIR>(seq.Elements[1]);
    }

    // ── Complex patterns ─────────────────────────────────────────────

    [Fact]
    public void Parse_WhitespacePattern_ReturnsCorrectTree()
    {
        // \s+ → OneOrMore(CharClass("[ \t\n\r\f\v]"))
        var result = RegexParser.Parse("\\s+");
        var oneOrMore = Assert.IsType<OneOrMoreIR>(result);
        var cc = Assert.IsType<CharClassIR>(oneOrMore.Inner);
        Assert.Equal("[ \\t\\n\\r\\f\\v]", cc.Text);
    }

    [Fact]
    public void Parse_NegatedNewlinePattern_ReturnsCorrectTree()
    {
        // [^\n]* → ZeroOrMore(CharClass("[^\n]"))
        var result = RegexParser.Parse("[^\\n]*");
        var zeroOrMore = Assert.IsType<ZeroOrMoreIR>(result);
        var cc = Assert.IsType<CharClassIR>(zeroOrMore.Inner);
        Assert.Equal("[^\\n]", cc.Text);
    }

    [Fact]
    public void Parse_IdentifierPattern_ReturnsCorrectTree()
    {
        // [a-zA-Z_][a-zA-Z0-9_]* → Sequence(CharClass, ZeroOrMore(CharClass))
        var result = RegexParser.Parse("[a-zA-Z_][a-zA-Z0-9_]*");
        var seq = Assert.IsType<SequenceIR>(result);
        Assert.Equal(2, seq.Elements.Count);

        var first = Assert.IsType<CharClassIR>(seq.Elements[0]);
        Assert.Equal("[a-zA-Z_]", first.Text);

        var second = Assert.IsType<ZeroOrMoreIR>(seq.Elements[1]);
        var secondInner = Assert.IsType<CharClassIR>(second.Inner);
        Assert.Equal("[a-zA-Z0-9_]", secondInner.Text);
    }

    [Fact]
    public void Parse_DigitPattern_ReturnsCorrectTree()
    {
        // \d+ → OneOrMore(CharClass("[0-9]"))
        var result = RegexParser.Parse("\\d+");
        var oneOrMore = Assert.IsType<OneOrMoreIR>(result);
        var cc = Assert.IsType<CharClassIR>(oneOrMore.Inner);
        Assert.Equal("[0-9]", cc.Text);
    }

    [Fact]
    public void Parse_DotStar_ReturnsCorrectTree()
    {
        // .* → ZeroOrMore(CharClass("[^\n]"))
        var result = RegexParser.Parse(".*");
        var zeroOrMore = Assert.IsType<ZeroOrMoreIR>(result);
        var cc = Assert.IsType<CharClassIR>(zeroOrMore.Inner);
        Assert.Equal("[^\\n]", cc.Text);
    }

    [Fact]
    public void Parse_GroupedAlternationWithQuantifier()
    {
        // (a|b)+ → OneOrMore(Alternation(a, b))
        var result = RegexParser.Parse("(a|b)+");
        var oneOrMore = Assert.IsType<OneOrMoreIR>(result);
        var alt = Assert.IsType<AlternationIR>(oneOrMore.Inner);
        Assert.Equal(2, alt.Alternatives.Count);
    }

    [Fact]
    public void Parse_EscapedSpecialChars_ReturnsLiterals()
    {
        // \+ should be literal '+'
        var result = RegexParser.Parse("\\+");
        var literal = Assert.IsType<StringLiteralIR>(result);
        Assert.Equal("+", literal.Value);
    }

    [Fact]
    public void Parse_EscapedParen_ReturnsLiteral()
    {
        var result = RegexParser.Parse("\\(");
        var literal = Assert.IsType<StringLiteralIR>(result);
        Assert.Equal("(", literal.Value);
    }

    [Fact]
    public void Parse_EscapedPipe_ReturnsLiteral()
    {
        var result = RegexParser.Parse("\\|");
        var literal = Assert.IsType<StringLiteralIR>(result);
        Assert.Equal("|", literal.Value);
    }

    // ── Error cases ──────────────────────────────────────────────────

    [Fact]
    public void Parse_UnterminatedCharClass_ThrowsFormatException()
    {
        _ = Assert.Throws<FormatException>(() => RegexParser.Parse("[abc"));
    }

    [Fact]
    public void Parse_UnterminatedGroup_ThrowsFormatException()
    {
        _ = Assert.Throws<FormatException>(() => RegexParser.Parse("(abc"));
    }

    [Fact]
    public void Parse_TrailingBackslash_ThrowsFormatException()
    {
        _ = Assert.Throws<FormatException>(() => RegexParser.Parse("\\"));
    }
}