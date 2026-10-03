using System.Collections.Generic;
using System.Linq;

namespace Storm.SourceGenerator.Emit;

public static partial class AstEmitter
{
    private static void EmitChildrenOverride(CodeWriter writer, List<PropertyInfo> properties)
    {
        writer.WriteBlock("public override IEnumerable<SyntaxNode> Children()", () =>
        {
            var yields = false;
            foreach(var property in properties.Where(property => property.IsChild))
            {
                yields |= EmitYields(writer, property.Name, property.IsNodeType, property.IsList, property.IsNullable || property.MayBeAbsent, property.Group, 0);
            }

            if(!yields)
            {
                writer.WriteLine("yield break;");
            }
        });
    }

    private static bool EmitYields(CodeWriter writer, string access, bool isNode, bool isList, bool mayBeNull, GroupStructModel? group, int depth)
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
                writer.WriteBlock($"foreach (var {item} in {access})", () => EmitFieldYields(writer, item, group, depth + 1));
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
            writer.WriteBlock($"foreach (var child in {access})", () => writer.WriteLine("yield return child;"));
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
            _ = EmitYields(writer, item + "." + field.Name, field.IsNodeType, field.IsList, field.IsNullable, field.Nested, depth);
        }
    }

    private static bool HoldsNodes(GroupStructModel group)
    {
        return group.Fields.Any(field => field.IsNodeType || (field.Nested != null && HoldsNodes(field.Nested)));
    }
}