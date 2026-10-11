using System;
using System.Collections.Generic;
using System.Linq;
using Storm.SourceGenerator.Frontend;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.IR;

/// <summary>
/// The identifier each declaration is generated under. A root name is kept, so the root grammar's generated API never
/// moves. A qualified name cannot be a parser method, node type or token kind as written, so its parts are joined by
/// underscores and numbered past any identifier already taken, or any type an identifier already taken generates.
/// </summary>
internal static class EmittedNames
{
    internal static IReadOnlyDictionary<string, string> Of(StormGrammar grammar)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach(var declaration in grammar.Declarations.Where(declaration => !IsQualified(declaration.Name)))
        {
            names[declaration.Name] = declaration.Name;
            _ = taken.Add(declaration.Name);
            types.UnionWith(TypesOf(declaration, declaration.Name));
        }

        foreach(var declaration in grammar.Declarations.Where(declaration => IsQualified(declaration.Name) && !names.ContainsKey(declaration.Name)))
        {
            var stem = declaration.Name.Replace('.', '_');
            var identifier = stem;
            for(var suffix = 2; taken.Contains(identifier) || TypesOf(declaration, identifier).Any(types.Contains); suffix++)
            {
                identifier = stem + suffix;
            }

            names[declaration.Name] = identifier;
            _ = taken.Add(identifier);
            types.UnionWith(TypesOf(declaration, identifier));
        }

        return names;
    }

    private static IEnumerable<string> TypesOf(StormDeclaration declaration, string identifier)
    {
        switch(declaration)
        {
            case StormTraitDecl:
                yield return "I" + identifier + "Node";
                break;

            case StormRuleDecl rule:
                yield return identifier + "Node";
                var alternatives = rule.Body is StormAlternationExpr alternation ? alternation.Alternatives : [rule.Body];
                foreach(var alternative in alternatives)
                {
                    var named = alternative as StormNamedAlternative;
                    yield return (named?.AlternativeName ?? "Default") + identifier + "Node";
                    if((named?.Expression ?? alternative) is StormInlineBlockExpr block)
                    {
                        foreach(var arm in block.Arms)
                        {
                            yield return arm.AlternativeName + identifier + "Node";
                        }
                    }
                }

                break;
        }
    }

    internal static string Of(IReadOnlyDictionary<string, string> names, string name)
    {
        return names.TryGetValue(name, out var identifier) ? identifier : name;
    }

    private static bool IsQualified(string name)
    {
        return name.IndexOf('.') >= 0;
    }
}