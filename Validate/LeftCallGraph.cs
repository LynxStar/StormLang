using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Storm.SourceGenerator.Frontend;

namespace Storm.SourceGenerator.Validate;

/// <summary>
/// The rules each rule's parse method can call before it consumes a token. A reference is passed over only
/// when its rule may succeed empty, a least fixed point over all rules. <c>E+</c> never succeeds empty, since
/// the emitted loop counts only rounds that consume. An inline block is walked as the emitter writes it -- the
/// prefix rule's body, then each arm rule's body after the prefix -- so neither rule is an edge, and a block or
/// arm <see cref="InlinePrefixVerification"/> rejects contributes nothing, that pass running after this one.
/// </summary>
internal sealed class LeftCallGraph
{
    private readonly Func<string, StormDeclaration?> lookup;
    private readonly Dictionary<StormRuleDecl, int> ids = new(ReferenceIdentity.Instance);
    private readonly HashSet<StormInlineBlockExpr> expanding = new(ReferenceIdentity.Instance);
    private readonly bool[] mayBeEmpty;
    private readonly List<int>[] calls;

    public LeftCallGraph(IEnumerable<StormDeclaration> declarations, Func<string, StormDeclaration?> lookup)
    {
        this.lookup = lookup;
        var rules = new List<StormRuleDecl>();
        foreach(var rule in declarations.OfType<StormRuleDecl>())
        {
            if(!ids.ContainsKey(rule))
            {
                ids[rule] = rules.Count;
                rules.Add(rule);
            }
        }

        Rules = rules;
        mayBeEmpty = new bool[rules.Count];
        var changed = true;
        while(changed)
        {
            changed = false;
            for(var rule = 0; rule < rules.Count; rule++)
            {
                if(!mayBeEmpty[rule] && Walk(rules[rule].Body, null))
                {
                    mayBeEmpty[rule] = true;
                    changed = true;
                }
            }
        }

        calls = new List<int>[rules.Count];
        for(var rule = 0; rule < rules.Count; rule++)
        {
            calls[rule] = [];
            _ = Walk(rules[rule].Body, calls[rule]);
        }
    }

    public IReadOnlyList<StormRuleDecl> Rules { get; }

    public IReadOnlyList<int> Calls(int rule)
    {
        return calls[rule];
    }

    private bool Walk(StormExpression expression, List<int>? found)
    {
        switch(expression)
        {
            case StormReferenceExpr reference:
                return Call(reference.Name, found);

            case StormQualifiedReferenceExpr reference:
                return Call(reference.Qualifier + "." + reference.Name, found);

            case StormSequenceExpr sequence:
                return WalkSequence(sequence.Elements, found);

            case StormAlternationExpr alternation:
                return WalkChoice(alternation.Alternatives, found);

            case StormOptionalExpr optional:
                _ = Walk(optional.Inner, found);
                return true;

            case StormZeroOrMoreExpr repetition:
                _ = Walk(repetition.Inner, found);
                return true;

            case StormPredicateExpr predicate:
                _ = Walk(predicate.Inner, found);
                return true;

            case StormOneOrMoreExpr repetition:
                _ = Walk(repetition.Inner, found);
                return false;

            case StormGroupExpr group:
                return Walk(group.Inner, found);

            case StormLabeledExpr labeled:
                return Walk(labeled.Element, found);

            case StormNamedAlternative alternative:
                return Walk(alternative.Expression, found);

            case StormInlineBlockExpr block:
                return WalkInlineBlock(block, found);

            default:
                return false;
        }
    }

    private bool Call(string name, List<int>? found)
    {
        if(lookup(name) is not StormRuleDecl rule || !ids.TryGetValue(rule, out var id))
        {
            return false;
        }

        if(found != null && !found.Contains(id))
        {
            found.Add(id);
        }

        return mayBeEmpty[id];
    }

    private bool WalkSequence(IEnumerable<StormExpression> elements, List<int>? found)
    {
        foreach(var element in elements)
        {
            if(!Walk(element, found))
            {
                return false;
            }
        }

        return true;
    }

    private bool WalkChoice(IEnumerable<StormExpression> alternatives, List<int>? found)
    {
        var any = false;
        foreach(var alternative in alternatives)
        {
            if(Walk(alternative, found))
            {
                any = true;
            }
        }

        return any;
    }

    private bool WalkInlineBlock(StormInlineBlockExpr block, List<int>? found)
    {
        if(block.Prefix is not StormReferenceExpr prefixReference
            || lookup(prefixReference.Name) is not StormRuleDecl prefix
            || prefix.Body is StormAlternationExpr
            || !expanding.Add(block))
        {
            return false;
        }

        var empty = Walk(prefix.Body, found) && WalkArms(block.Arms, prefixReference.Name, Elements(prefix.Body), found);
        _ = expanding.Remove(block);
        return empty;
    }

    private bool WalkArms(IEnumerable<StormInlineArm> arms, string prefixName, IReadOnlyList<StormExpression> prefixElements, List<int>? found)
    {
        var any = false;
        foreach(var arm in arms)
        {
            var empty = arm.Expression is StormInlineBlockExpr nested
                ? WalkInlineBlock(nested, found)
                : Tail(arm, prefixName, prefixElements) is { } tail && WalkSequence(tail, found);
            if(empty)
            {
                any = true;
            }
        }

        return any;
    }

    private IEnumerable<StormExpression>? Tail(StormInlineArm arm, string prefixName, IReadOnlyList<StormExpression> prefixElements)
    {
        if(arm.Expression is not StormReferenceExpr armReference
            || lookup(armReference.Name) is not StormRuleDecl target
            || target.Body is StormAlternationExpr)
        {
            return null;
        }

        var elements = Elements(target.Body);
        if(elements.Count > 0 && Unlabelled(elements[0]) is StormReferenceExpr first && first.Name == prefixName)
        {
            return elements.Skip(1);
        }

        if(elements.Count < prefixElements.Count)
        {
            return null;
        }

        for(var i = 0; i < prefixElements.Count; i++)
        {
            if(!SameShape(Unlabelled(prefixElements[i]), Unlabelled(elements[i])))
            {
                return null;
            }
        }

        return elements.Skip(prefixElements.Count);
    }

    private static IReadOnlyList<StormExpression> Elements(StormExpression body)
    {
        return body is StormSequenceExpr sequence ? sequence.Elements : [body];
    }

    private static StormExpression Unlabelled(StormExpression element)
    {
        return element is StormLabeledExpr labeled ? labeled.Element : element;
    }

    private static bool SameShape(StormExpression left, StormExpression right)
    {
        return (left, right) switch
        {
            (StormReferenceExpr a, StormReferenceExpr b) => a.Name == b.Name,
            (StormStringLiteralExpr a, StormStringLiteralExpr b) => a.Value == b.Value,
            (StormOneOrMoreExpr a, StormOneOrMoreExpr b) => SameShape(a.Inner, b.Inner),
            (StormZeroOrMoreExpr a, StormZeroOrMoreExpr b) => SameShape(a.Inner, b.Inner),
            (StormOptionalExpr a, StormOptionalExpr b) => SameShape(a.Inner, b.Inner),
            _ => false,
        };
    }

    private sealed class ReferenceIdentity : IEqualityComparer<object>
    {
        public static readonly ReferenceIdentity Instance = new();

        public new bool Equals(object x, object y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(object obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}