using System;
using System.Collections.Generic;
using System.Linq;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.IR;

/// <summary>
/// The identifier each declaration is generated under. A root name is kept, so the root grammar's generated API never
/// moves. A qualified name cannot be a parser method, node type or token kind as written, so its parts are joined by
/// underscores and numbered past any identifier already taken.
/// </summary>
internal static class EmittedNames
{
    internal static IReadOnlyDictionary<string, string> Of(StormGrammar grammar)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach(var declaration in grammar.Declarations.Where(declaration => !IsQualified(declaration.Name)))
        {
            names[declaration.Name] = declaration.Name;
            _ = taken.Add(declaration.Name);
        }

        foreach(var declaration in grammar.Declarations.Where(declaration => IsQualified(declaration.Name) && !names.ContainsKey(declaration.Name)))
        {
            var stem = declaration.Name.Replace('.', '_');
            var identifier = stem;
            for(var suffix = 2; !taken.Add(identifier); suffix++)
            {
                identifier = stem + suffix;
            }

            names[declaration.Name] = identifier;
        }

        return names;
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