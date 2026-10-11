using System;
using System.Collections.Generic;
using System.Linq;
using Storm.SourceGenerator.Emit;

namespace Storm.SourceGenerator.IR;

/// <summary>
/// A literal in a rule names the token the lexer produces for its text: the earliest default-mode token that accepts
/// the whole text, since the lexer breaks equal-length ties by declaration order. A synthetic token is made only where
/// no declared token accepts the text, because one appended after them would lose every such tie. When the token
/// produced is a pattern rather than the literal's own spelling, the parser also requires the lexeme, so a longer
/// lexeme of that pattern is never split to force the literal.
/// </summary>
internal sealed class LiteralTokens
{
    private readonly List<TokenIR> declared;
    private readonly Dictionary<string, (TokenIR Token, bool Exact)?> lexed = new(StringComparer.Ordinal);
    private Nfa? lexer;

    internal LiteralTokens(IEnumerable<TokenIR> tokens)
    {
        declared = tokens.Where(token => !token.IsSynthetic && token.Mode == "default").OrderBy(token => token.DeclarationIndex).ToList();
    }

    internal Dictionary<string, TokenIR> Synthetic { get; } = new(StringComparer.Ordinal);

    internal static TokenRefIR? Lexed(string literal, IEnumerable<TokenIR> tokens)
    {
        return new LiteralTokens(tokens).Lexed(literal);
    }

    internal TokenRefIR? Lexed(string literal)
    {
        if(!lexed.TryGetValue(literal, out var found))
        {
            found = Lex(literal);
            lexed[literal] = found;
        }

        if(found is not { } token)
        {
            return null;
        }

        return token.Exact ? new TokenRefIR(token.Token) : new TokenRefIR(token.Token, literal);
    }

    private (TokenIR Token, bool Exact)? Lex(string literal)
    {
        if(literal.Length == 0 || declared.Count == 0)
        {
            return null;
        }

        lexer ??= new NfaBuilder().BuildCombined(declared);
        var states = Closure([lexer.StartState]);
        foreach(var character in literal)
        {
            states = Closure(states
                .SelectMany(state => state.Transitions)
                .Where(transition => !transition.IsEpsilon && transition.Ranges!.Any(range => range.Contains(character)))
                .Select(transition => transition.Target));
        }

        var accepted = states.Where(state => state.AcceptingTokenIndex >= 0).Select(state => state.AcceptingTokenIndex).ToList();
        if(accepted.Count == 0)
        {
            return null;
        }

        var winner = declared.First(token => token.DeclarationIndex == accepted.Min());
        return (winner, Spells(winner.Expression, literal));
    }

    private static HashSet<NfaState> Closure(IEnumerable<NfaState> states)
    {
        var closure = new HashSet<NfaState>();
        var pending = new Stack<NfaState>(states);
        while(pending.Count > 0)
        {
            var state = pending.Pop();
            if(!closure.Add(state))
            {
                continue;
            }

            foreach(var transition in state.Transitions.Where(transition => transition.IsEpsilon))
            {
                pending.Push(transition.Target);
            }
        }

        return closure;
    }

    private static bool Spells(ExpressionIR pattern, string literal)
    {
        return pattern switch
        {
            StringLiteralIR text => text.Value == literal,
            InlineFragmentIR fragment => Spells(fragment.Body, literal),
            _ => false,
        };
    }
}