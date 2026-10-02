using System.Collections.Generic;
using System.Linq;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Emit;

/// <summary>Lowering runs these refusals because the emitters have no diagnostic channel.</summary>
internal sealed class UnemittableShapes
{
    private readonly string rule;
    private readonly IReadOnlyList<FieldLabelIR> labels;
    private readonly Dictionary<SequenceIR, AnonymousGroupInfo> groups;
    private readonly List<string> found;

    private UnemittableShapes(string rule, IReadOnlyList<FieldLabelIR> labels, IEnumerable<AnonymousGroupInfo> groups, List<string> found)
    {
        this.rule = rule;
        this.labels = labels;
        this.groups = groups.ToDictionary(group => group.Sequence);
        this.found = found;
    }

    internal static IReadOnlyList<string> Find(string rule, RuleAlternativeIR alternative)
    {
        var found = new List<string>();
        if(alternative.Expression is InlineBlockIR block)
        {
            InlineBlock(rule, block, found);
        }
        else
        {
            new UnemittableShapes(rule, alternative.FieldLabels, AstEmitter.DetectAnonymousGroups(alternative), found).Walk(alternative.Expression, null);
        }

        return found.Distinct().ToList();
    }

    private static void InlineBlock(string rule, InlineBlockIR block, List<string> found)
    {
        var prefix = new UnemittableShapes(rule, block.PrefixLabels, [], found);
        foreach(var element in block.PrefixElements)
        {
            prefix.Walk(element, null);
        }

        foreach(var arm in block.Arms)
        {
            if(arm.TailExpression is InlineBlockIR nested)
            {
                InlineBlock(rule, nested, found);
            }
            else
            {
                new UnemittableShapes(rule, arm.TailLabels, [], found).Walk(arm.TailExpression, null);
            }
        }
    }

    private void Walk(ExpressionIR expr, ExpressionIR? structuralHome)
    {
        var label = LabelOf(expr);
        if(Inner(expr) is SequenceIR wrapped && groups.TryGetValue(wrapped, out var group))
        {
            Group(wrapped, group.Labels, "");
            return;
        }

        switch(expr)
        {
            case SequenceIR when label != null:
                found.Add($"The label '{label}' in rule '{rule}' is on a group with no ?, * or +: label an element inside it instead");
                break;

            case SequenceIR sequence:
                foreach(var element in sequence.Elements)
                {
                    Walk(element, structuralHome);
                }

                break;

            case AlternationIR alternation when !alternation.IsTokenChoice:
                found.Add(label == null
                    ? $"An unlabelled alternation in rule '{rule}' must be a choice between single tokens: give any other choice a rule of its own"
                    : $"The labelled alternation '{label}' in rule '{rule}' must be a choice between single tokens: give any other choice a rule of its own");
                break;

            case AlternationIR alternation:
                foreach(var alternative in alternation.Alternatives)
                {
                    Walk(alternative, structuralHome);
                }

                break;

            case OptionalIR or ZeroOrMoreIR or OneOrMoreIR:
                Walk(Inner(expr)!, structuralHome);
                break;

            case RuleRefIR reference when structuralHome != null && label == null && reference != structuralHome:
                found.Add($"Rule '{rule}' parses an unlabelled '{reference.Rule.Name}' inside a repeated or optional group, where no field of the group keeps its node: label it");
                break;

            case TokenRefIR { Token.IsHidden: true } tokenRef:
                found.Add($"Rule '{rule}' names the hidden token '{tokenRef.Token.Name}', which the parser never sees");
                break;

            case TokenRefIR or StringLiteralIR or RuleRefIR or PredicateIR:
                break;

            default:
                var what = expr switch
                {
                    CharClassIR => "a character class",
                    RegexLiteralIR => "a regular expression",
                    InlineBlockIR => "an inline block",
                    InlineFragmentIR => "a fragment",
                    _ => "a " + expr.GetType().Name,
                };
                found.Add($"Rule '{rule}' uses {what} where the parser cannot match it");
                break;
        }
    }

    private void Group(SequenceIR sequence, IReadOnlyList<FieldLabelIR> groupLabels, string structName)
    {
        var fields = AstEmitter.GetGroupStructFields(structName, sequence, groupLabels);
        foreach(var element in sequence.Elements)
        {
            var field = fields.FirstOrDefault(candidate => ReferenceEquals(candidate.Element, element));
            if(field.Element == null)
            {
                Walk(element, sequence);
            }
            else if(field.Kind == GroupFieldKind.NestedGroup)
            {
                Group(field.NestedSequence!, groupLabels, field.NestedStructName!);
            }
            else if(field.Kind == GroupFieldKind.Other)
            {
                Walk(element, AstEmitter.Representative(element, groupLabels, []) ?? sequence);
            }
            else
            {
                Walk(element, null);
            }
        }
    }

    private static ExpressionIR? Inner(ExpressionIR expr)
    {
        return expr switch
        {
            OptionalIR optional => optional.Inner,
            ZeroOrMoreIR zeroOrMore => zeroOrMore.Inner,
            OneOrMoreIR oneOrMore => oneOrMore.Inner,
            _ => null,
        };
    }

    private string? LabelOf(ExpressionIR expr)
    {
        return labels.FirstOrDefault(label => ReferenceEquals(label.Expression, expr))?.Label;
    }
}