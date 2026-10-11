using System;
using System.Collections.Generic;
using System.Linq;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;

namespace Storm.SourceGenerator.Resolve;

/// <summary>
/// Names each declaration the root reaches exactly once: one the root sees by its own name, any other by the first
/// alias path reaching it breadth first. A file is resolved once (R6.7), so two aliases of it name the same
/// declarations, and the namespaced imports of a namespaced file nest under its alias.
/// </summary>
internal sealed class EffectiveNames
{
    private readonly ImportResolutionResult resolution;
    private readonly Dictionary<string, ImportScope> scopes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<StormDiagnostic> scopeDiagnostics = [];
    private readonly List<StormDiagnostic> spellingDiagnostics = [];
    private readonly Queue<(string Qualifier, ImportScope Scope)> pending = new();

    private EffectiveNames(ImportResolutionResult resolution)
    {
        this.resolution = resolution;
        Root = ScopeOf(resolution.RootPath);
    }

    internal ImportScope Root { get; }

    internal Dictionary<StormDeclaration, string> Names { get; } = new(DeclarationIdentity.Instance);

    internal List<(StormDeclaration Declaration, ImportScope Scope, string FilePath)> Owners { get; } = [];

    internal Dictionary<string, StormDeclaration> Spellings { get; } = new(StringComparer.Ordinal);

    internal IEnumerable<StormDiagnostic> Diagnostics => scopeDiagnostics.Distinct().Concat(spellingDiagnostics);

    internal static EffectiveNames Assign(ImportResolutionResult resolution)
    {
        var names = new EffectiveNames(resolution);
        names.Enter(null, names.Root);
        var entered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { names.Root.EntryPath };
        while(names.pending.Count > 0)
        {
            var (qualifier, scope) = names.pending.Dequeue();
            if(entered.Add(scope.EntryPath))
            {
                names.Enter(qualifier, scope);
            }
        }

        return names;
    }

    internal ImportScope ScopeOf(string path)
    {
        if(!scopes.TryGetValue(path, out var scope))
        {
            scope = ImportScope.Collect(path, resolution, scopeDiagnostics);
            scopes[path] = scope;
        }

        return scope;
    }

    private void Enter(string? qualifier, ImportScope scope)
    {
        foreach(var (name, filePath, declaration) in scope.Declarations)
        {
            if(!Names.ContainsKey(declaration))
            {
                Names[declaration] = qualifier == null ? name : qualifier + "." + name;
                Owners.Add((declaration, scope, filePath));
            }
        }

        foreach(var (alias, path) in scope.Aliases)
        {
            var prefix = qualifier == null ? alias : qualifier + "." + alias;
            var imported = ScopeOf(path);
            foreach(var (name, filePath, declaration) in imported.Declarations)
            {
                Spell(prefix + "." + name, filePath, declaration);
            }

            pending.Enqueue((prefix, imported));
        }
    }

    private void Spell(string spelled, string filePath, StormDeclaration declaration)
    {
        if(!Spellings.TryGetValue(spelled, out var existing))
        {
            Spellings[spelled] = declaration;
        }
        else if(!ReferenceEquals(existing, declaration))
        {
            spellingDiagnostics.Add(new StormDiagnostic(
                StormDiagnosticIds.STORM_DUPLICATE_NAME,
                StormDiagnosticSeverity.Error,
                $"Duplicate qualified declaration name '{spelled}': first declared at {existing.Line}:{existing.Column}, redeclared at {declaration.Line}:{declaration.Column} in '{filePath}'",
                filePath,
                declaration.Line,
                declaration.Column));
        }
    }
}