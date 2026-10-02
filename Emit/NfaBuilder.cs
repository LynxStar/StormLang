using System;
using System.Collections.Generic;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Emit;

/// <summary>
/// Converts <see cref="ExpressionIR"/> trees into NFA fragments using
/// Thompson's construction. Also builds combined NFAs for sets of tokens.
/// <para>
/// Each instance maintains its own state-ID counter, so concurrent
/// source-generation invocations do not share mutable state.
/// </para>
/// </summary>
internal sealed class NfaBuilder
{
    private int nextStateId;

    /// <summary>
    /// Creates a fresh NFA state with a unique ID.
    /// </summary>
    private NfaState NewState()
    {
        return new NfaState(nextStateId++);
    }

    // ── BuildFragment overloads (Thompson's construction) ────────────

    /// <summary>
    /// Builds an NFA fragment for a <see cref="StringLiteralIR"/>.
    /// Creates a chain of states connected by single-character transitions.
    /// </summary>
    public NfaFragment BuildFragment(StringLiteralIR literal)
    {
        var start = NewState();
        var current = start;

        foreach(var ch in literal.Value)
        {
            var next = NewState();
            current.Transitions.Add(new NfaTransition(
                new[] { new CharRange(ch) }, next));
            current = next;
        }

        return new NfaFragment(start, current);
    }

    /// <summary>
    /// Builds an NFA fragment for a <see cref="CharClassIR"/>.
    /// Parses the char class text and creates a single transition with
    /// the appropriate <see cref="CharRange"/> array.
    /// </summary>
    public NfaFragment BuildFragment(CharClassIR charClass)
    {
        var ranges = ParseCharClassRanges(charClass.Text);
        var start = NewState();
        var accept = NewState();
        start.Transitions.Add(new NfaTransition(ranges, accept));
        return new NfaFragment(start, accept);
    }

    /// <summary>
    /// Builds an NFA fragment for a <see cref="SequenceIR"/>.
    /// Concatenates sub-fragments by connecting each accept state to the
    /// next start state via epsilon transitions.
    /// </summary>
    public NfaFragment BuildFragment(SequenceIR sequence)
    {
        if(sequence.Elements.Count == 0)
        {
            // Empty sequence: single epsilon fragment
            var s = NewState();
            var a = NewState();
            s.Transitions.Add(new NfaTransition(a));
            return new NfaFragment(s, a);
        }

        NfaFragment? combined = null;

        foreach(var element in sequence.Elements)
        {
            var frag = BuildFragment(element);

            if(combined == null)
            {
                combined = frag;
            }
            else
            {
                // Connect previous accept to this fragment's start via epsilon
                combined.Accept.Transitions.Add(new NfaTransition(frag.Start));
                combined = new NfaFragment(combined.Start, frag.Accept);
            }
        }

        return combined!;
    }

    /// <summary>
    /// Builds an NFA fragment for an <see cref="AlternationIR"/>.
    /// Creates a new start state with epsilon transitions to each
    /// alternative's start, and each alternative's accept with epsilon
    /// to a new shared accept state.
    /// </summary>
    public NfaFragment BuildFragment(AlternationIR alternation)
    {
        var start = NewState();
        var accept = NewState();

        foreach(var alt in alternation.Alternatives)
        {
            var frag = BuildFragment(alt);
            start.Transitions.Add(new NfaTransition(frag.Start));
            frag.Accept.Transitions.Add(new NfaTransition(accept));
        }

        return new NfaFragment(start, accept);
    }

    /// <summary>
    /// Builds an NFA fragment for an <see cref="OptionalIR"/>.
    /// Builds the inner fragment and adds an epsilon from start to accept
    /// (allowing the empty match).
    /// </summary>
    public NfaFragment BuildFragment(OptionalIR optional)
    {
        var inner = BuildFragment(optional.Inner);
        var start = NewState();
        var accept = NewState();

        // Path through inner expression
        start.Transitions.Add(new NfaTransition(inner.Start));
        inner.Accept.Transitions.Add(new NfaTransition(accept));

        // Skip path (empty match)
        start.Transitions.Add(new NfaTransition(accept));

        return new NfaFragment(start, accept);
    }

    /// <summary>
    /// Builds an NFA fragment for a <see cref="ZeroOrMoreIR"/>.
    /// Builds the inner fragment, adds epsilon from accept back to start
    /// (loop) and from new start to new accept (skip).
    /// </summary>
    public NfaFragment BuildFragment(ZeroOrMoreIR zeroOrMore)
    {
        var inner = BuildFragment(zeroOrMore.Inner);
        var start = NewState();
        var accept = NewState();

        // Enter inner
        start.Transitions.Add(new NfaTransition(inner.Start));
        // Skip (zero occurrences)
        start.Transitions.Add(new NfaTransition(accept));
        // Loop back
        inner.Accept.Transitions.Add(new NfaTransition(inner.Start));
        // Exit
        inner.Accept.Transitions.Add(new NfaTransition(accept));

        return new NfaFragment(start, accept);
    }

    /// <summary>
    /// Builds an NFA fragment for a <see cref="OneOrMoreIR"/>.
    /// Builds the inner fragment and adds epsilon from accept back to start
    /// (loop), but no skip epsilon — at least one match is required.
    /// </summary>
    public NfaFragment BuildFragment(OneOrMoreIR oneOrMore)
    {
        var inner = BuildFragment(oneOrMore.Inner);
        var start = NewState();
        var accept = NewState();

        // Enter inner
        start.Transitions.Add(new NfaTransition(inner.Start));
        // Loop back
        inner.Accept.Transitions.Add(new NfaTransition(inner.Start));
        // Exit
        inner.Accept.Transitions.Add(new NfaTransition(accept));

        return new NfaFragment(start, accept);
    }

    /// <summary>
    /// Builds an NFA fragment for an <see cref="InlineFragmentIR"/>.
    /// Delegates to <see cref="BuildFragment(ExpressionIR)"/> on the body.
    /// </summary>
    public NfaFragment BuildFragment(InlineFragmentIR inlineFragment)
    {
        return BuildFragment(inlineFragment.Body);
    }

    /// <summary>
    /// Builds an NFA fragment for a <see cref="TokenRefIR"/>.
    /// Delegates to <see cref="BuildFragment(ExpressionIR)"/> on the
    /// referenced token's expression.
    /// </summary>
    public NfaFragment BuildFragment(TokenRefIR tokenRef)
    {
        return BuildFragment(tokenRef.Token.Expression);
    }

    /// <summary>
    /// Top-level dispatch: builds an NFA fragment for any
    /// <see cref="ExpressionIR"/> by switching on its concrete type.
    /// </summary>
    public NfaFragment BuildFragment(ExpressionIR expression)
    {
        return expression switch
        {
            StringLiteralIR literal => BuildFragment(literal),
            CharClassIR charClass => BuildFragment(charClass),
            SequenceIR sequence => BuildFragment(sequence),
            AlternationIR alternation => BuildFragment(alternation),
            OptionalIR optional => BuildFragment(optional),
            ZeroOrMoreIR zeroOrMore => BuildFragment(zeroOrMore),
            OneOrMoreIR oneOrMore => BuildFragment(oneOrMore),
            InlineFragmentIR inlineFragment => BuildFragment(inlineFragment),
            TokenRefIR tokenRef => BuildFragment(tokenRef),
            RegexLiteralIR regex => BuildFragment(RegexParser.Parse(regex.Pattern)),
            _ => throw new NotSupportedException(
                                    $"Unsupported ExpressionIR type: {expression.GetType().Name}"),
        };
    }

    /// <summary>
    /// Top-level convenience method: builds an NFA fragment for a single
    /// <see cref="ExpressionIR"/> and wraps it in a complete <see cref="Nfa"/>.
    /// </summary>
    public Nfa Build(ExpressionIR expression)
    {
        var fragment = BuildFragment(expression);
        return new Nfa(fragment.Start);
    }

    /// <summary>
    /// Builds a combined NFA for all tokens in a mode.
    /// Creates a shared start state with epsilon transitions to each
    /// token's NFA, and annotates each token NFA's accept state with
    /// the token's declaration index.
    /// </summary>
    public Nfa BuildCombined(IReadOnlyList<TokenIR> tokens)
    {
        var start = NewState();

        foreach(var token in tokens)
        {
            var fragment = BuildFragment(token.Expression);
            fragment.Accept.AcceptingTokenIndex = token.DeclarationIndex;
            start.Transitions.Add(new NfaTransition(fragment.Start));
        }

        return new Nfa(start);
    }

    // ── Char class parsing ───────────────────────────────────────────
    // Adapted from LexerEmitter.ParseCharClass to produce CharRange[]
    // compatible with the NFA transition model.

    /// <summary>
    /// Parses a character class text (e.g. <c>[a-zA-Z_]</c> or <c>[^0-9]</c>)
    /// and returns the corresponding <see cref="CharRange"/> array.
    /// Handles negation by computing the complement ranges over the
    /// full UTF-16 range (0–65535).
    /// </summary>
    internal static CharRange[] ParseCharClassRanges(string text)
    {
        var negated = false;
        var ranges = new List<CharRange>();

        // Strip outer [ ]
        if(text.Length < 2 || text[0] != '[')
        {
            return Array.Empty<CharRange>();
        }

        var inner = text.Substring(1, text.Length - 2);

        var i = 0;
        if(inner.Length > 0 && inner[0] == '^')
        {
            negated = true;
            i = 1;
        }

        while(i < inner.Length)
        {
            var ch = ReadCharClassChar(inner, ref i);

            if(i < inner.Length && inner[i] == '-' && i + 1 < inner.Length)
            {
                i++; // skip -
                var ch2 = ReadCharClassChar(inner, ref i);
                ranges.Add(new CharRange(ch, ch2));
            }
            else
            {
                ranges.Add(new CharRange(ch));
            }
        }

        if(negated)
        {
            ranges = ComputeNegatedRanges(ranges);
        }

        return ranges.ToArray();
    }

    /// <summary>
    /// Reads a single character from a char class body, handling escape
    /// sequences like <c>\n</c>, <c>\t</c>, <c>\\</c>, etc.
    /// </summary>
    private static char ReadCharClassChar(string text, ref int i)
    {
        if(i >= text.Length)
        {
            return '\0';
        }

        if(text[i] == '\\' && i + 1 < text.Length)
        {
            i++; // skip backslash
            var escaped = text[i];
            i++;
            return escaped switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'f' => '\f',
                'v' => '\v',
                '0' => '\0',
                '\\' => '\\',
                '"' => '"',
                '\'' => '\'',
                ']' => ']',
                '[' => '[',
                '-' => '-',
                '^' => '^',
                _ => escaped,
            };
        }

        var c = text[i];
        i++;
        return c;
    }

    /// <summary>
    /// Given a list of positive ranges, computes the complement over
    /// the full char range ['\0', '\uFFFF']. The result covers every
    /// character NOT in any of the input ranges.
    /// </summary>
    private static List<CharRange> ComputeNegatedRanges(List<CharRange> positiveRanges)
    {
        // Sort by Lo, then Hi
        positiveRanges.Sort((a, b) =>
        {
            var cmp = a.Lo.CompareTo(b.Lo);
            return cmp != 0 ? cmp : a.Hi.CompareTo(b.Hi);
        });

        // Merge overlapping/adjacent ranges
        var merged = new List<CharRange>();
        foreach(var r in positiveRanges)
        {
            if(merged.Count > 0)
            {
                var last = merged[merged.Count - 1];
                // Adjacent or overlapping: last.Hi + 1 >= r.Lo
                if(last.Hi >= r.Lo || last.Hi + 1 >= r.Lo)
                {
                    merged[merged.Count - 1] = new CharRange(
                        last.Lo, (char)Math.Max(last.Hi, r.Hi));
                    continue;
                }
            }

            merged.Add(r);
        }

        // Build complement
        var result = new List<CharRange>();
        var next = 0; // next char to potentially include

        foreach(var r in merged)
        {
            if(next < r.Lo)
            {
                result.Add(new CharRange((char)next, (char)(r.Lo - 1)));
            }

            next = r.Hi + 1;
        }

        if(next <= 0xFFFF)
        {
            result.Add(new CharRange((char)next, '\uFFFF'));
        }

        return result;
    }
}