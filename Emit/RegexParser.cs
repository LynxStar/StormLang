using System;
using System.Collections.Generic;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Emit;

/// <summary>
/// Parses regex pattern strings into <see cref="ExpressionIR"/> trees
/// so they can be fed into Thompson's construction via <see cref="NfaBuilder"/>.
/// <para>
/// Supports: character classes (<c>[a-z]</c>, <c>[^abc]</c>), quantifiers
/// (<c>+</c>, <c>*</c>, <c>?</c>), escape sequences (<c>\s</c>, <c>\d</c>,
/// <c>\w</c>, <c>\n</c>, <c>\t</c>, etc.), dot metacharacter (<c>.</c>),
/// grouping with parentheses, and alternation with <c>|</c>.
/// </para>
/// <para>
/// Grammar (lowest to highest precedence):
/// <code>
/// expr        → alternation
/// alternation → concat ('|' concat)*
/// concat      → quantified+
/// quantified  → atom ('+'|'*'|'?')?
/// atom        → literal | charClass | '(' expr ')' | '.' | escape
/// </code>
/// </para>
/// </summary>
internal static class RegexParser
{
    /// <summary>
    /// Parse a regex pattern string into an <see cref="ExpressionIR"/> tree.
    /// </summary>
    public static ExpressionIR Parse(string pattern)
    {
        var parser = new Parser(pattern);
        var result = parser.ParseExpression();

        if(parser.Position < pattern.Length)
        {
            throw new FormatException(
                $"Unexpected character '{pattern[parser.Position]}' at position {parser.Position} in regex pattern: {pattern}");
        }

        return result;
    }

    /// <summary>
    /// Internal stateful parser that tracks position within the pattern.
    /// </summary>
    private sealed class Parser
    {
        private readonly string pattern;

        public int Position { get; private set; }

        public Parser(string pattern)
        {
            this.pattern = pattern;
            Position = 0;
        }

        private bool HasMore => Position < pattern.Length;

        private char Peek()
        {
            return pattern[Position];
        }

        private char Advance()
        {
            return pattern[Position++];
        }

        /// <summary>
        /// Entry point: parses a full expression (alternation level).
        /// </summary>
        public ExpressionIR ParseExpression()
        {
            return ParseAlternation();
        }

        /// <summary>
        /// Parses alternation: concat ('|' concat)*
        /// </summary>
        private ExpressionIR ParseAlternation()
        {
            var first = ParseConcatenation();

            if(!HasMore || Peek() != '|')
            {
                return first;
            }

            var alternatives = new List<ExpressionIR> { first };

            while(HasMore && Peek() == '|')
            {
                _ = Advance(); // consume '|'
                alternatives.Add(ParseConcatenation());
            }

            return new AlternationIR(alternatives);
        }

        /// <summary>
        /// Parses concatenation: quantified+
        /// </summary>
        private ExpressionIR ParseConcatenation()
        {
            var elements = new List<ExpressionIR>();

            while(HasMore && Peek() != '|' && Peek() != ')')
            {
                elements.Add(ParseQuantified());
            }

            if(elements.Count == 0)
            {
                // Empty concatenation (e.g., empty alternative in "a|")
                // Return an empty string literal
                return new StringLiteralIR("");
            }

            if(elements.Count == 1)
            {
                return elements[0];
            }

            return new SequenceIR(elements);
        }

        /// <summary>
        /// Parses quantified: atom ('+'|'*'|'?')?
        /// </summary>
        private ExpressionIR ParseQuantified()
        {
            var atom = ParseAtom();

            if(!HasMore)
            {
                return atom;
            }

            switch(Peek())
            {
                case '+':
                    _ = Advance();
                    return new OneOrMoreIR(atom);
                case '*':
                    _ = Advance();
                    return new ZeroOrMoreIR(atom);
                case '?':
                    _ = Advance();
                    return new OptionalIR(atom);
                default:
                    return atom;
            }
        }

        /// <summary>
        /// Parses an atom: literal, char class, group, dot, or escape.
        /// </summary>
        private ExpressionIR ParseAtom()
        {
            if(!HasMore)
            {
                throw new FormatException(
                    $"Unexpected end of regex pattern: {pattern}");
            }

            var ch = Peek();

            switch(ch)
            {
                case '[':
                    return ParseCharClass();

                case '(':
                    return ParseGroup();

                case '.':
                    _ = Advance();
                    return new CharClassIR("[^\\n]");

                case '\\':
                    return ParseEscape();

                default:
                    _ = Advance();
                    return new StringLiteralIR(ch.ToString());
            }
        }

        /// <summary>
        /// Parses a character class: [a-z], [^abc], [a-zA-Z0-9_], etc.
        /// Extracts the full bracket expression and creates a CharClassIR.
        /// </summary>
        private ExpressionIR ParseCharClass()
        {
            var startPos = Position;
            _ = Advance(); // consume '['

            // Handle negation
            if(HasMore && Peek() == '^')
            {
                _ = Advance();
            }

            // Handle ']' as first character in class (literal ']')
            if(HasMore && Peek() == ']')
            {
                _ = Advance();
            }

            // Read until closing ']'
            while(HasMore && Peek() != ']')
            {
                if(Peek() == '\\' && Position + 1 < pattern.Length)
                {
                    _ = Advance(); // skip backslash
                    _ = Advance(); // skip escaped char
                }
                else
                {
                    _ = Advance();
                }
            }

            if(!HasMore)
            {
                throw new FormatException(
                    $"Unterminated character class starting at position {startPos} in regex pattern: {pattern}");
            }

            _ = Advance(); // consume ']'

            var classText = pattern.Substring(startPos, Position - startPos);
            return new CharClassIR(classText);
        }

        /// <summary>
        /// Parses a parenthesized group: '(' expr ')'
        /// </summary>
        private ExpressionIR ParseGroup()
        {
            _ = Advance(); // consume '('

            var inner = ParseExpression();

            if(!HasMore || Peek() != ')')
            {
                throw new FormatException(
                    $"Unterminated group in regex pattern: {pattern}");
            }

            _ = Advance(); // consume ')'

            return inner;
        }

        /// <summary>
        /// Parses an escape sequence. For shorthand classes (\s, \d, \w)
        /// at the top level, converts to equivalent CharClassIR. For
        /// literal escapes (\n, \t, \r, \0, \\), creates StringLiteralIR.
        /// </summary>
        private ExpressionIR ParseEscape()
        {
            _ = Advance(); // consume '\'

            if(!HasMore)
            {
                throw new FormatException(
                    $"Unexpected end of regex pattern after backslash: {pattern}");
            }

            var escaped = Advance();

            return escaped switch
            {
                // Shorthand character classes → CharClassIR
                's' => new CharClassIR("[ \\t\\n\\r\\f\\v]"),
                'S' => new CharClassIR("[^ \\t\\n\\r\\f\\v]"),
                'd' => new CharClassIR("[0-9]"),
                'D' => new CharClassIR("[^0-9]"),
                'w' => new CharClassIR("[a-zA-Z0-9_]"),
                'W' => new CharClassIR("[^a-zA-Z0-9_]"),
                // Literal escape sequences → StringLiteralIR
                'n' => new StringLiteralIR("\n"),
                't' => new StringLiteralIR("\t"),
                'r' => new StringLiteralIR("\r"),
                '0' => new StringLiteralIR("\0"),
                '\\' => new StringLiteralIR("\\"),
                // Any other escaped character is treated as a literal
                _ => new StringLiteralIR(escaped.ToString()),
            };
        }
    }
}