using System;
using System.Collections.Generic;
using System.Linq;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Emit;

internal readonly record struct NodeSlot(string Name, string TypeName, ExpressionIR Source, bool Appends);

public static partial class AstEmitter
{
    internal static IReadOnlyList<NodeSlot> NodeSlots(RuleIR rule, RuleAlternativeIR alternative, string nodeTypeName)
    {
        return FilledProperties(rule, alternative, nodeTypeName)
            .Select(property => new NodeSlot(property.Name, property.TypeName, property.Source!, property.Appends))
            .ToList();
    }

    private sealed record NodeShape(RuleAlternativeIR Alternative, List<PropertyInfo> Properties);

    private static IEnumerable<PropertyInfo> FilledProperties(RuleIR rule, RuleAlternativeIR alternative, string nodeTypeName)
    {
        var shape = HasSharedAlternativeNode(rule) && rule.Alternatives.Contains(alternative)
            ? CollectNodeShapes(rule, nodeTypeName, false, out _, out _).First(candidate => candidate.Alternative == alternative).Properties
            : CollectProperties(alternative, nodeTypeName, false, out _, out _);

        return shape.Where(property => property.Source != null);
    }

    private static bool HasSharedAlternativeNode(RuleIR rule)
    {
        return !rule.Alternatives.Any(candidate => candidate.AlternativeName != null || candidate.Expression is InlineBlockIR);
    }

    // Declaration names are shared where their types and group shapes agree; each alternative keeps its own ordered
    // ownership plan so a forwarding label can be an owner in another alternative without yielding either node twice.
    private static List<NodeShape> CollectNodeShapes(RuleIR rule, string nodeTypeName, bool emitObsoleteShims, out List<GroupStructModel> groupStructs, out List<ObsoleteShim> shims)
    {
        var shapes = new List<NodeShape>();
        var declarations = new Dictionary<(string Name, string TypeName), PropertyInfo>();
        var usedNames = new HashSet<string>();
        var groupNames = new Dictionary<(int Index, string Shape), string>();
        var allGroups = new List<GroupStructModel>();
        var allShims = new List<ObsoleteShim>();
        groupStructs = allGroups;
        shims = allShims;
        for(var index = 0; index < rule.Alternatives.Count; index++)
        {
            string nameOf(AnonymousGroupInfo group)
            {
                var key = (group.GroupIndex, GroupShape(group.Sequence, group.Labels));
                if(!groupNames.TryGetValue(key, out var name))
                {
                    name = "Group" + group.GroupIndex;
                    if(groupNames.Values.Contains(name))
                    {
                        name += "Alternative" + (index + 1);
                    }

                    groupNames[key] = name;
                }

                return name;
            }

            var alternative = rule.Alternatives[index];
            var properties = CollectProperties(alternative, nodeTypeName, emitObsoleteShims, out var groups, out var aliases, nameOf);
            foreach(var property in properties)
            {
                var key = (property.Name, property.TypeName);
                if(!declarations.TryGetValue(key, out var declared))
                {
                    property.Name = EnsureUnique(property.Name, usedNames);
                    declarations[key] = property;
                }
                else
                {
                    property.Name = declared.Name;
                }
            }

            allGroups.AddRange(groups.Where(group => !allGroups.Any(existing => existing.StructName == group.StructName)));
            allShims.AddRange(aliases.Where(alias => !allShims.Any(existing => existing.PropertyName == alias.PropertyName) && !usedNames.Contains(alias.PropertyName)));
            shapes.Add(new NodeShape(alternative, properties));
        }

        _ = allShims.RemoveAll(alias => usedNames.Contains(alias.PropertyName));
        return shapes;
    }

    private static string GroupShape(SequenceIR sequence, IReadOnlyList<FieldLabelIR> labels)
    {
        return string.Join(";", GetGroupStructFields("", sequence, labels).Select(field => field.Kind == GroupFieldKind.NestedGroup
            ? field.FieldName + ":" + field.NestedModifier + "(" + GroupShape(field.NestedSequence!, labels) + ")"
            : field.FieldName + ":" + field.FieldType));
    }

    internal static ExpressionIR? Representative(ExpressionIR labelled, ICollection<ExpressionIR> groupSequences)
    {
        return labelled switch
        {
            OptionalIR { Inner: SequenceIR group } when groupSequences.Contains(group) => group.Elements.OfType<RuleRefIR>().FirstOrDefault(),
            OptionalIR { Inner: SequenceIR sequence } => sequence.Elements.Any(element => element is RuleRefIR)
                ? sequence.Elements.OfType<RuleRefIR>().First()
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

    // A label on an optional or repeated group forwards a node that a capture inside the group holds -- a group struct's
    // field, or the reference's own label -- and only that capture yields it from Children(). The exception is a labelled
    // reference a repetition overwrites: it keeps only the last round, so the group's label is what holds every node.
    private static bool Forwards(ExpressionIR labelled, ExpressionIR? source, IReadOnlyList<FieldLabelIR> labels, ICollection<ExpressionIR> groupSequences, ICollection<RuleRefIR> repeatedRuleRefs)
    {
        var inner = labelled switch
        {
            OptionalIR optional => optional.Inner,
            ZeroOrMoreIR zeroOrMore => zeroOrMore.Inner,
            OneOrMoreIR oneOrMore => oneOrMore.Inner,
            _ => null,
        };

        return inner != null && source is RuleRefIR reference
            && (groupSequences.Contains(inner) || (FindLabel(reference, labels) != null && !repeatedRuleRefs.Contains(reference)));
    }

    private static PropertyInfo RepeatedLabelProperty(ExpressionIR labelled, string propName)
    {
        var elementType = GetPropertyInfo(labelled, propName).TypeName.TrimEnd('?');
        var isNode = elementType != "Token";

        return new PropertyInfo(propName, $"ImmutableArray<{elementType}>", false, isNode, true, $" = ImmutableArray<{elementType}>.Empty;");
    }

    private static void CollectInside(ExpressionIR expr, Func<ExpressionIR, bool> opens, bool inside, HashSet<ExpressionIR> result)
    {
        if(inside)
        {
            _ = result.Add(expr);
        }

        var nested = inside || opens(expr);
        switch(expr)
        {
            case SequenceIR sequence:
                foreach(var element in sequence.Elements)
                {
                    CollectInside(element, opens, nested, result);
                }

                break;
            case AlternationIR alternation:
                foreach(var alternative in alternation.Alternatives)
                {
                    CollectInside(alternative, opens, nested, result);
                }

                break;
            case OptionalIR optional:
                CollectInside(optional.Inner, opens, nested, result);
                break;
            case ZeroOrMoreIR zeroOrMore:
                CollectInside(zeroOrMore.Inner, opens, nested, result);
                break;
            case OneOrMoreIR oneOrMore:
                CollectInside(oneOrMore.Inner, opens, nested, result);
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
                ? Representative(expr, groupSequences)
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