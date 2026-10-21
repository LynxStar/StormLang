using System;
using System.Collections.Generic;
using System.Linq;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Emit;

public static partial class ParserEmitter
{
    // Lowering creates fresh expression instances for the shared prefix and each tail.
    // Keep the target rule's capture names/types, but feed them from those parsed instances.
    private sealed record InlineArmProjection(RuleAlternativeIR Alternative, IReadOnlyList<NodeSlot> Slots, RuleRefIR? PrefixReference);

    private static InlineArmProjection ProjectInlineArm(InlineBlockIR block, InlineArmIR arm, Func<ExpressionIR, ExpressionIR> resolve)
    {
        var alternative = arm.TargetRule.Alternatives[0];
        var body = SequenceElements(alternative.Expression);
        var nominal = body.FirstOrDefault() is RuleRefIR reference && reference.Rule == block.PrefixRule ? reference : null;
        var prefixLength = nominal == null ? block.PrefixElements.Count : 1;
        var map = new Dictionary<ExpressionIR, ExpressionIR>();
        if(nominal == null)
        {
            MapExpressions(body.Take(prefixLength).ToList(), block.PrefixElements, map);
        }

        MapExpressions(body.Skip(prefixLength).ToList(), SequenceElements(arm.TailExpression), map);
        ResolveAll(map, resolve);
        var mapped = MapAlternative(alternative, map);
        var slots = AstEmitter.NodeSlots(arm.TargetRule, alternative, arm.TargetRule.Name + "Node")
            .Select(slot => slot with { Source = map.TryGetValue(slot.Source, out var source) ? source : slot.Source })
            .ToList();
        return new InlineArmProjection(mapped, slots, nominal);
    }

    private static InlineArmProjection ProjectInlinePrefix(InlineBlockIR block, Func<ExpressionIR, ExpressionIR> resolve)
    {
        var alternative = block.PrefixRule.Alternatives[0];
        var map = new Dictionary<ExpressionIR, ExpressionIR>();
        MapExpressions(SequenceElements(alternative.Expression), block.PrefixElements, map);
        ResolveAll(map, resolve);
        var slots = AstEmitter.NodeSlots(block.PrefixRule, alternative, block.PrefixRule.Name + "Node")
            .Select(slot => slot with { Source = map.TryGetValue(slot.Source, out var source) ? source : slot.Source })
            .ToList();
        return new InlineArmProjection(MapAlternative(alternative, map), slots, null);
    }

    private static void ResolveAll(Dictionary<ExpressionIR, ExpressionIR> map, Func<ExpressionIR, ExpressionIR> resolve)
    {
        foreach(var original in map.Keys.ToList())
        {
            map[original] = resolve(map[original]);
        }
    }

    private static IReadOnlyList<ExpressionIR> SequenceElements(ExpressionIR expression)
    {
        return expression is SequenceIR sequence ? sequence.Elements : [expression];
    }

    private static void MapExpressions(IReadOnlyList<ExpressionIR> original, IReadOnlyList<ExpressionIR> parsed, Dictionary<ExpressionIR, ExpressionIR> map)
    {
        if(original.Count != parsed.Count)
        {
            throw new InvalidOperationException("An admitted inline prefix must preserve its expression structure.");
        }

        for(var i = 0; i < original.Count; i++)
        {
            map[original[i]] = parsed[i];
            MapExpressions(ExpressionChildren(original[i]), ExpressionChildren(parsed[i]), map);
        }
    }

    private static IReadOnlyList<ExpressionIR> ExpressionChildren(ExpressionIR expression)
    {
        return expression switch
        {
            SequenceIR sequence => sequence.Elements,
            AlternationIR alternation => alternation.Alternatives,
            OptionalIR optional => [optional.Inner],
            ZeroOrMoreIR repetition => [repetition.Inner],
            OneOrMoreIR repetition => [repetition.Inner],
            PredicateIR predicate => [predicate.Inner],
            _ => Array.Empty<ExpressionIR>(),
        };
    }

    private static RuleAlternativeIR MapAlternative(RuleAlternativeIR alternative, Dictionary<ExpressionIR, ExpressionIR> map)
    {
        var expression = alternative.Expression is SequenceIR sequence
            ? new SequenceIR(sequence.Elements.Select(element => map.TryGetValue(element, out var parsed) ? parsed : element).ToList())
            : map.TryGetValue(alternative.Expression, out var parsed) ? parsed : alternative.Expression;
        var labels = alternative.FieldLabels.Select(label => new FieldLabelIR(label.Label,
            map.TryGetValue(label.Expression, out var source) ? source : label.Expression)).ToList();
        return new RuleAlternativeIR(alternative.AlternativeName, labels, expression);
    }

    private static Frame MergeInlineFrames(IEnumerable<Frame> frames, HashSet<ExpressionIR> prefixSources, string groupPrefix)
    {
        var merged = new Frame(null);
        var groupIndex = 0;
        foreach(var frame in frames)
        {
            foreach(var group in frame.Groups.Where(group => prefixSources.Contains(group.Key)))
            {
                foreach(var plan in group.Value)
                {
                    if(!merged.Groups.TryGetValue(group.Key, out var plans))
                    {
                        plans = new List<GroupPlan>();
                        merged.Groups[group.Key] = plans;
                    }

                    plans.Add(new GroupPlan(plan.StructName, plan.StructName, groupPrefix + groupIndex++, group.Key, plan.Labels));
                }
            }

            foreach(var slot in frame.Slots)
            {
                merged.Slots.Add(slot);
            }

            foreach(var source in prefixSources)
            {
                foreach(var feed in frame.FeedsOf(source))
                {
                    merged.Feed(source, feed.Slot, feed.Appends);
                }
            }
        }

        return merged;
    }
}