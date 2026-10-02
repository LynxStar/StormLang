using System.Collections.Generic;
using System.Linq;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Emit;

internal readonly record struct NodeSlot(string Name, string TypeName, ExpressionIR Source, bool Appends);

public static partial class AstEmitter
{
    internal static IReadOnlyList<NodeSlot> NodeSlots(RuleIR rule, RuleAlternativeIR alternative, string nodeTypeName)
    {
        var slots = CollectProperties(alternative, nodeTypeName, false, out _, out _)
            .Where(property => property.Source != null)
            .Select(property => new NodeSlot(property.Name, property.TypeName, property.Source!, property.Appends))
            .ToList();

        var sharesFirstAlternativeNode = !rule.Alternatives.Any(candidate => candidate.AlternativeName != null || candidate.Expression is InlineBlockIR);
        if(!sharesFirstAlternativeNode || alternative == rule.Alternatives[0])
        {
            return slots;
        }

        var declared = new HashSet<(string Name, string TypeName)>(CollectProperties(rule.Alternatives[0], nodeTypeName, false, out _, out _)
            .Select(property => (property.Name, property.TypeName)));

        return slots.Where(slot => declared.Contains((slot.Name, slot.TypeName))).ToList();
    }

    internal static ExpressionIR? Representative(ExpressionIR labelled, IReadOnlyList<FieldLabelIR> labels, ICollection<ExpressionIR> groupSequences)
    {
        return labelled switch
        {
            OptionalIR { Inner: SequenceIR group } when groupSequences.Contains(group) => group.Elements.OfType<RuleRefIR>().FirstOrDefault(),
            OptionalIR { Inner: SequenceIR sequence } => sequence.Elements.Any(element => element is RuleRefIR)
                ? sequence.Elements.OfType<RuleRefIR>().FirstOrDefault(reference => !labels.Any(label => ReferenceEquals(label.Expression, reference)))
                : sequence.Elements.FirstOrDefault(element => Leaf(element) is not (null or RuleRefIR)),
            OptionalIR optional => Leaf(optional.Inner),
            ZeroOrMoreIR { Inner: SequenceIR sequence } => ListRepresentative(sequence),
            OneOrMoreIR { Inner: SequenceIR sequence } => ListRepresentative(sequence),
            ZeroOrMoreIR zeroOrMore => Leaf(zeroOrMore.Inner),
            OneOrMoreIR oneOrMore => Leaf(oneOrMore.Inner),
            _ => Leaf(labelled),
        };
    }

    private static ExpressionIR? ListRepresentative(SequenceIR sequence)
    {
        return (ExpressionIR?)sequence.Elements.OfType<RuleRefIR>().FirstOrDefault() ?? sequence.Elements.OfType<TokenRefIR>().LastOrDefault();
    }

    private static ExpressionIR? Leaf(ExpressionIR expr)
    {
        return expr is TokenRefIR or RuleRefIR or StringLiteralIR or AlternationIR { IsTokenChoice: true } ? expr : null;
    }

    private static PropertyInfo RepeatedLabelProperty(ExpressionIR labelled, string propName, IReadOnlyList<FieldLabelIR> labels, ICollection<ExpressionIR> groupSequences)
    {
        var elementType = GetPropertyInfo(labelled, propName).TypeName.TrimEnd('?');
        var isNode = elementType != "Token";

        return new PropertyInfo(propName, $"ImmutableArray<{elementType}>", false, isNode, true, $" = ImmutableArray<{elementType}>.Empty;")
            .FilledBy(Representative(labelled, labels, groupSequences), true);
    }

    private static void CollectInsideRepetition(ExpressionIR expr, bool insideRep, HashSet<ExpressionIR> result)
    {
        if(insideRep)
        {
            _ = result.Add(expr);
        }

        switch(expr)
        {
            case SequenceIR sequence:
                foreach(var element in sequence.Elements)
                {
                    CollectInsideRepetition(element, insideRep, result);
                }

                break;
            case AlternationIR alternation:
                foreach(var alternative in alternation.Alternatives)
                {
                    CollectInsideRepetition(alternative, insideRep, result);
                }

                break;
            case OptionalIR optional:
                CollectInsideRepetition(optional.Inner, insideRep, result);
                break;
            case ZeroOrMoreIR zeroOrMore:
                CollectInsideRepetition(zeroOrMore.Inner, true, result);
                break;
            case OneOrMoreIR oneOrMore:
                CollectInsideRepetition(oneOrMore.Inner, true, result);
                break;
        }
    }

    private static void CollectNestedProperties(
        ExpressionIR expr,
        IReadOnlyList<FieldLabelIR> labels,
        List<PropertyInfo> properties,
        HashSet<string> usedNames,
        HashSet<ExpressionIR> groupSequences,
        bool repeated,
        ExpressionIR? fed)
    {
        var labelled = labels.Any(label => ReferenceEquals(label.Expression, expr));
        var inner = expr switch
        {
            OptionalIR optional => optional.Inner,
            ZeroOrMoreIR zeroOrMore => zeroOrMore.Inner,
            OneOrMoreIR oneOrMore => oneOrMore.Inner,
            _ => null,
        };

        if(inner != null)
        {
            if(groupSequences.Contains(inner))
            {
                return;
            }

            var repeats = repeated || expr is not OptionalIR;
            var representative = labelled
                ? Representative(expr, labels, groupSequences)
                : expr is OptionalIR ? UnlabelledReference(inner, labels) : RepeatedRuleRef(inner);
            if(!labelled && representative is RuleRefIR reference && reference != fed)
            {
                properties.Add(NestedReferenceProperty(reference, repeats, usedNames));
            }

            CollectNestedProperties(inner, labels, properties, usedNames, groupSequences, repeats, representative ?? fed);
            return;
        }

        if(labelled)
        {
            return;
        }

        switch(expr)
        {
            case SequenceIR sequence:
                foreach(var element in sequence.Elements)
                {
                    CollectNestedProperties(element, labels, properties, usedNames, groupSequences, repeated, fed);
                }

                break;

            case RuleRefIR reference when reference != fed:
                properties.Add(NestedReferenceProperty(reference, repeated, usedNames));
                break;
        }
    }

    private static RuleRefIR? UnlabelledReference(ExpressionIR optionalInner, IReadOnlyList<FieldLabelIR> labels)
    {
        var candidates = optionalInner is SequenceIR sequence ? sequence.Elements : [optionalInner];

        return candidates.OfType<RuleRefIR>().FirstOrDefault(reference => !labels.Any(label => ReferenceEquals(label.Expression, reference)));
    }

    private static PropertyInfo NestedReferenceProperty(RuleRefIR reference, bool repeated, HashSet<string> usedNames)
    {
        var nodeType = reference.Rule.Name + "Node";
        if(repeated)
        {
            var listName = EnsureUnique(PascalCase(reference.Rule.Name) + "s", usedNames);
            return new PropertyInfo(listName, $"ImmutableArray<{nodeType}>", false, true, true, $" = ImmutableArray<{nodeType}>.Empty;").FilledBy(reference, true);
        }

        var name = EnsureUnique(PascalCase(reference.Rule.Name), usedNames);
        return new PropertyInfo(name, nodeType + "?", true, true, false, "").FilledBy(reference, false);
    }
}