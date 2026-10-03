using System;
using System.Collections.Generic;
using System.Linq;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Emit;

public static partial class AstEmitter
{
    private static void CaptureOrder(ExpressionIR expression, Dictionary<ExpressionIR, int> order)
    {
        if(order.ContainsKey(expression))
        {
            return;
        }

        order[expression] = order.Count;
        var children = expression switch
        {
            SequenceIR sequence => sequence.Elements,
            AlternationIR alternation => alternation.Alternatives,
            OptionalIR optional => new[] { optional.Inner },
            ZeroOrMoreIR repeated => new[] { repeated.Inner },
            OneOrMoreIR repeated => new[] { repeated.Inner },
            _ => Array.Empty<ExpressionIR>(),
        };
        foreach(var child in children)
        {
            CaptureOrder(child, order);
        }
    }

    private static void EmitAlternativeChildren(CodeWriter writer, List<NodeShape> shapes)
    {
        if(shapes.Count == 1)
        {
            EmitChildrenOverride(writer, shapes[0].Properties);
            return;
        }

        writer.WriteBlock("public override IEnumerable<SyntaxNode> Children()", () => writer.WriteBlock("switch (__StormAlternative)", () =>
            {
                for(var index = 0; index < shapes.Count; index++)
                {
                    writer.WriteLine($"case {index}:");
                    writer.Indent();
                    foreach(var property in shapes[index].Properties.Where(property => property.IsChild))
                    {
                        _ = EmitYields(writer, property.Name, property.IsNodeType, property.IsList, property.IsNullable || property.MayBeAbsent, property.Group, 0, false);
                    }

                    writer.WriteLine("yield break;");
                    writer.Dedent();
                }
            }));
    }

    private static void EmitChildrenOverride(CodeWriter writer, List<PropertyInfo> properties)
    {
        writer.WriteBlock("public override IEnumerable<SyntaxNode> Children()", () =>
        {
            var yields = false;
            foreach(var property in properties.Where(property => property.IsChild))
            {
                yields |= EmitYields(writer, property.Name, property.IsNodeType, property.IsList, property.IsNullable || property.MayBeAbsent, property.Group, 0, false);
            }

            if(!yields)
            {
                writer.WriteLine("yield break;");
            }
        });
    }

    private static bool EmitYields(CodeWriter writer, string access, bool isNode, bool isList, bool mayBeNull, GroupStructModel? group, int depth, bool inStruct)
    {
        if(group != null)
        {
            if(!HoldsNodes(group))
            {
                return false;
            }

            if(isList)
            {
                var item = depth == 0 ? "groupItem" : "groupItem" + depth;
                EmitLoop(writer, access, item, inStruct, () => EmitFieldYields(writer, item, group, depth + 1));
            }
            else if(mayBeNull)
            {
                writer.WriteBlock($"if ({access} != null)", () => EmitFieldYields(writer, access + ".Value", group, depth));
            }
            else
            {
                EmitFieldYields(writer, access, group, depth);
            }

            return true;
        }

        if(!isNode)
        {
            return false;
        }

        if(isList)
        {
            EmitLoop(writer, access, "child", inStruct, () => writer.WriteLine("yield return child;"));
        }
        else if(mayBeNull)
        {
            writer.WriteBlock($"if ({access} != null)", () => writer.WriteLine($"yield return {access};"));
        }
        else
        {
            writer.WriteLine($"yield return {access};");
        }

        return true;
    }

    private static void EmitFieldYields(CodeWriter writer, string item, GroupStructModel group, int depth)
    {
        foreach(var field in group.Fields)
        {
            _ = EmitYields(writer, item + "." + field.Name, field.IsNodeType, field.IsList, field.IsNullable, field.Nested, depth, true);
        }
    }

    // A node's list property starts empty, but a group struct's list field has no initializer: a struct built without it
    // holds a default array, which cannot be enumerated.
    private static void EmitLoop(CodeWriter writer, string access, string item, bool inStruct, Action body)
    {
        if(inStruct)
        {
            writer.WriteBlock($"if (!{access}.IsDefault)", () => writer.WriteBlock($"foreach (var {item} in {access})", body));
        }
        else
        {
            writer.WriteBlock($"foreach (var {item} in {access})", body);
        }
    }

    private static bool HoldsNodes(GroupStructModel group)
    {
        return group.Fields.Any(field => field.IsNodeType || (field.Nested != null && HoldsNodes(field.Nested)));
    }
}