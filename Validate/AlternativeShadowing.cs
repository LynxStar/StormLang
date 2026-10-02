using System;
using System.Collections.Generic;
using System.Linq;
using Storm.SourceGenerator.Frontend;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Validate;

internal sealed class AlternativeShadowing
{
    private const int MaxDepth = 16;
    private const int MaxPrefixes = 256;

    private readonly StormGrammar grammar;

    internal AlternativeShadowing(StormGrammar grammar)
    {
        this.grammar = grammar;
    }

    private enum Ending
    {
        Complete,
        Truncated,
        Stopped,
    }

    private enum Outcome
    {
        Succeeds,
        Fails,
        Unknown,
    }

    internal bool IsShadowed(IReadOnlyList<StormExpression> alternatives, int index)
    {
        for(var depth = 1; depth <= MaxDepth; depth++)
        {
            var prefixes = new Prefixes(this, depth).Of(alternatives[index]);
            if(prefixes == null)
            {
                return false;
            }

            var deeper = false;
            foreach(var prefix in prefixes)
            {
                var proof = new Prover(this, prefix.Tokens).Earliest(alternatives, index);
                if(proof.Outcome == Outcome.Succeeds)
                {
                    continue;
                }

                if(prefix.Ending != Ending.Truncated || !proof.ReadPastEnd)
                {
                    return false;
                }

                deeper = true;
            }

            if(!deeper)
            {
                return true;
            }
        }

        return false;
    }

    private string? Terminal(StormExpression expr)
    {
        if(expr is StormStringLiteralExpr literal)
        {
            return "\"" + literal.Value;
        }

        return Declared(expr) is { Declaration: StormTokenDecl or StormHiddenTokenDecl } token ? token.Name : null;
    }

    private (string Name, StormRuleDecl Rule)? Rule(StormExpression expr)
    {
        return Declared(expr) is { Declaration: StormRuleDecl rule } declared ? (declared.Name, rule) : null;
    }

    private (string Name, StormDeclaration? Declaration)? Declared(StormExpression expr)
    {
        var name = expr switch
        {
            StormReferenceExpr reference => reference.Name,
            StormQualifiedReferenceExpr qualified => qualified.Qualifier + "." + qualified.Name,
            _ => null,
        };

        return name == null ? null : (name, grammar.LookupByName(name));
    }

    /// <summary>A literal is never taken to differ: it may be the declared token that spells it.</summary>
    private static bool Distinct(string token, string other)
    {
        return token != other && token[0] != '"' && other[0] != '"';
    }

    private sealed class Prefix : IEquatable<Prefix>
    {
        internal Prefix(IReadOnlyList<string> tokens, Ending ending)
        {
            Tokens = tokens;
            Ending = ending;
        }

        internal IReadOnlyList<string> Tokens { get; }

        internal Ending Ending { get; }

        public bool Equals(Prefix? other)
        {
            return other != null && Ending == other.Ending && Tokens.SequenceEqual(other.Tokens);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as Prefix);
        }

        public override int GetHashCode()
        {
            return Tokens.Aggregate((int)Ending, (hash, token) => hash * 31 + token.GetHashCode());
        }
    }

    private sealed class Prefixes
    {
        private static readonly Prefix Empty = new([], Ending.Complete);
        private static readonly Prefix Stopped = new([], Ending.Stopped);

        private readonly AlternativeShadowing owner;
        private readonly int depth;
        private readonly HashSet<string> expanding = [];
        private bool overflowed;

        internal Prefixes(AlternativeShadowing owner, int depth)
        {
            this.owner = owner;
            this.depth = depth;
        }

        internal IReadOnlyCollection<Prefix>? Of(StormExpression alternative)
        {
            var prefixes = Expand(alternative, depth);
            return overflowed ? null : prefixes;
        }

        private HashSet<Prefix> Expand(StormExpression expr, int budget)
        {
            if(overflowed)
            {
                return [];
            }

            if(owner.Terminal(expr) is { } token)
            {
                return [budget == 0 ? new Prefix([], Ending.Truncated) : new Prefix([token], Ending.Complete)];
            }

            switch(expr)
            {
                case StormSequenceExpr sequence:
                    var prefixes = new HashSet<Prefix> { Empty };
                    foreach(var element in sequence.Elements)
                    {
                        prefixes = Then(prefixes, budget, rest => Expand(element, rest), false);
                    }

                    return prefixes;

                case StormAlternationExpr alternation:
                    return Union(alternation.Alternatives.Select(alternative => Expand(alternative, budget)));

                case StormOptionalExpr optional:
                    return Union([[Empty], Expand(optional.Inner, budget)]);

                case StormZeroOrMoreExpr zeroOrMore:
                    return Rounds(zeroOrMore.Inner, budget);

                case StormOneOrMoreExpr oneOrMore:
                    return Then(Expand(oneOrMore.Inner, budget), budget, rest => Rounds(oneOrMore.Inner, rest), true);

                case StormPredicateExpr:
                    return [Empty];

                case StormGroupExpr group:
                    return Expand(group.Inner, budget);

                case StormLabeledExpr labeled:
                    return Expand(labeled.Element, budget);

                case StormNamedAlternative named:
                    return Expand(named.Expression, budget);

                case StormInlineBlockExpr block:
                    return new HashSet<Prefix>(Expand(block.Prefix, budget)
                        .Select(prefix => prefix.Ending == Ending.Complete ? new Prefix(prefix.Tokens, Ending.Stopped) : prefix));
            }

            if(owner.Rule(expr) is not { } rule || !expanding.Add(rule.Name))
            {
                return [Stopped];
            }

            var body = Expand(rule.Rule.Body, budget);
            _ = expanding.Remove(rule.Name);
            return body;
        }

        private HashSet<Prefix> Rounds(StormExpression inner, int budget)
        {
            return Union([[Empty], Then(Expand(inner, budget), budget, rest => Rounds(inner, rest), true)]);
        }

        private HashSet<Prefix> Then(HashSet<Prefix> heads, int budget, Func<int, HashSet<Prefix>> tail, bool stopsWithoutProgress)
        {
            var result = new HashSet<Prefix>();
            var tails = new Dictionary<int, HashSet<Prefix>>();
            foreach(var head in heads)
            {
                if(head.Ending != Ending.Complete || (stopsWithoutProgress && head.Tokens.Count == 0))
                {
                    Add(result, head);
                    continue;
                }

                var rest = budget - head.Tokens.Count;
                if(!tails.TryGetValue(rest, out var following))
                {
                    following = tail(rest);
                    tails[rest] = following;
                }

                foreach(var next in following)
                {
                    Add(result, new Prefix([.. head.Tokens, .. next.Tokens], next.Ending));
                }
            }

            return result;
        }

        private HashSet<Prefix> Union(IEnumerable<HashSet<Prefix>> sets)
        {
            var result = new HashSet<Prefix>();
            foreach(var prefix in sets.SelectMany(set => set))
            {
                Add(result, prefix);
            }

            return result;
        }

        private void Add(HashSet<Prefix> prefixes, Prefix prefix)
        {
            if(prefixes.Add(prefix) && prefixes.Count > MaxPrefixes)
            {
                overflowed = true;
            }
        }
    }

    private readonly record struct Proof(Outcome Outcome, int End, bool ReadPastEnd);

    private sealed class Prover
    {
        private readonly AlternativeShadowing owner;
        private readonly IReadOnlyList<string> tokens;
        private readonly Dictionary<(string Rule, int Position), Proof> rules = new();

        internal Prover(AlternativeShadowing owner, IReadOnlyList<string> tokens)
        {
            this.owner = owner;
            this.tokens = tokens;
        }

        internal Proof Earliest(IReadOnlyList<StormExpression> alternatives, int index)
        {
            var readPastEnd = false;
            foreach(var alternative in alternatives.Take(index))
            {
                var proof = Prove(alternative, 0);
                if(proof.Outcome == Outcome.Succeeds)
                {
                    return proof;
                }

                readPastEnd |= proof.ReadPastEnd;
            }

            return new Proof(Outcome.Unknown, -1, readPastEnd);
        }

        private Proof Prove(StormExpression expr, int position)
        {
            if(owner.Terminal(expr) is { } token)
            {
                return Terminal(token, position);
            }

            switch(expr)
            {
                case StormSequenceExpr sequence:
                    return Sequence(sequence.Elements, position);

                case StormAlternationExpr alternation:
                    return Choice(alternation.Alternatives, position);

                case StormOptionalExpr optional:
                    var inner = Prove(optional.Inner, position);
                    return inner.Outcome switch
                    {
                        Outcome.Succeeds => inner,
                        Outcome.Fails => new Proof(Outcome.Succeeds, position, inner.ReadPastEnd),
                        _ => new Proof(Outcome.Succeeds, -1, inner.ReadPastEnd),
                    };

                case StormZeroOrMoreExpr zeroOrMore:
                    return Rounds(zeroOrMore.Inner, position, false);

                case StormOneOrMoreExpr oneOrMore:
                    return Rounds(oneOrMore.Inner, position, true);

                case StormPredicateExpr predicate:
                    var guarded = Prove(predicate.Inner, position);
                    if(guarded.Outcome == Outcome.Unknown)
                    {
                        return guarded with { End = -1 };
                    }

                    var holds = predicate.IsNegated ? guarded.Outcome == Outcome.Fails : guarded.Outcome == Outcome.Succeeds;
                    return new Proof(holds ? Outcome.Succeeds : Outcome.Fails, holds ? position : -1, guarded.ReadPastEnd);

                case StormGroupExpr group:
                    return Prove(group.Inner, position);

                case StormLabeledExpr labeled:
                    return Prove(labeled.Element, position);

                case StormNamedAlternative named:
                    return Prove(named.Expression, position);

                case StormInlineBlockExpr block:
                    var prefix = Prove(block.Prefix, position);
                    return prefix.Outcome == Outcome.Succeeds ? prefix with { End = -1 } : prefix;
            }

            return owner.Rule(expr) is { } rule ? Rule(rule.Name, rule.Rule, position) : new Proof(Outcome.Unknown, -1, false);
        }

        private Proof Terminal(string token, int position)
        {
            if(position >= tokens.Count)
            {
                return new Proof(Outcome.Unknown, -1, true);
            }

            if(tokens[position] == token)
            {
                return new Proof(Outcome.Succeeds, position + 1, false);
            }

            return new Proof(Distinct(tokens[position], token) ? Outcome.Fails : Outcome.Unknown, -1, false);
        }

        private Proof Sequence(IReadOnlyList<StormExpression> elements, int position)
        {
            var end = position;
            var readPastEnd = false;
            foreach(var element in elements)
            {
                if(end < 0)
                {
                    if(!AlwaysSucceeds(element))
                    {
                        return new Proof(Outcome.Unknown, -1, readPastEnd);
                    }

                    continue;
                }

                var proof = Prove(element, end);
                readPastEnd |= proof.ReadPastEnd;
                if(proof.Outcome != Outcome.Succeeds)
                {
                    return proof with { ReadPastEnd = readPastEnd };
                }

                end = proof.End;
            }

            return new Proof(Outcome.Succeeds, end, readPastEnd);
        }

        private Proof Choice(IReadOnlyList<StormExpression> alternatives, int position)
        {
            var undecided = false;
            var readPastEnd = false;
            foreach(var alternative in alternatives)
            {
                var proof = Prove(alternative, position);
                readPastEnd |= proof.ReadPastEnd;
                if(proof.Outcome == Outcome.Succeeds)
                {
                    return new Proof(Outcome.Succeeds, undecided ? -1 : proof.End, readPastEnd);
                }

                undecided |= proof.Outcome == Outcome.Unknown;
            }

            return new Proof(undecided ? Outcome.Unknown : Outcome.Fails, -1, readPastEnd);
        }

        private Proof Rounds(StormExpression inner, int position, bool atLeastOnce)
        {
            var end = position;
            var readPastEnd = false;
            while(true)
            {
                var round = Prove(inner, end);
                readPastEnd |= round.ReadPastEnd;
                if(round.Outcome == Outcome.Succeeds && round.End > end)
                {
                    end = round.End;
                    atLeastOnce = false;
                    continue;
                }

                var stops = round.Outcome == Outcome.Fails || (round.Outcome == Outcome.Succeeds && round.End == end);
                if(atLeastOnce)
                {
                    return new Proof(stops ? Outcome.Fails : Outcome.Unknown, -1, readPastEnd);
                }

                return new Proof(Outcome.Succeeds, stops ? end : -1, readPastEnd);
            }
        }

        private Proof Rule(string name, StormRuleDecl rule, int position)
        {
            if(rules.TryGetValue((name, position), out var known))
            {
                return known;
            }

            rules[(name, position)] = new Proof(Outcome.Unknown, -1, false);
            var proof = Prove(rule.Body, position);
            rules[(name, position)] = proof;
            return proof;
        }

        private static bool AlwaysSucceeds(StormExpression expr)
        {
            return expr switch
            {
                StormOptionalExpr or StormZeroOrMoreExpr => true,
                StormGroupExpr group => AlwaysSucceeds(group.Inner),
                StormLabeledExpr labeled => AlwaysSucceeds(labeled.Element),
                StormSequenceExpr sequence => sequence.Elements.All(AlwaysSucceeds),
                StormAlternationExpr alternation => alternation.Alternatives.Any(AlwaysSucceeds),
                StormPredicateExpr { IsNegated: false } predicate => AlwaysSucceeds(predicate.Inner),
                _ => false,
            };
        }
    }
}