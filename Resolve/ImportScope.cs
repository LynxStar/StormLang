using System;
using System.Collections.Generic;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;

namespace Storm.SourceGenerator.Resolve;

/// <summary>
/// The names one file sees: its own declarations merged with those of its unnamespaced imports, depth first with each
/// file's imports ahead of its own declarations, and the aliases of its namespaced imports, including those its
/// unnamespaced imports bring in.
/// </summary>
internal sealed class ImportScope
{
    private readonly Dictionary<string, (string FilePath, StormDeclaration Declaration)> names = new(StringComparer.Ordinal);

    private ImportScope(string entryPath)
    {
        EntryPath = entryPath;
    }

    internal string EntryPath { get; }

    internal List<(string Name, string FilePath, StormDeclaration Declaration)> Declarations { get; } = [];

    internal List<(string Alias, string Path)> Aliases { get; } = [];

    internal static ImportScope Collect(string entryPath, ImportResolutionResult resolution, List<StormDiagnostic> diagnostics)
    {
        var scope = new ImportScope(entryPath);
        scope.Merge(entryPath, resolution, new HashSet<string>(StringComparer.OrdinalIgnoreCase), diagnostics);
        return scope;
    }

    internal StormDeclaration? Find(string name)
    {
        return names.TryGetValue(name, out var found) ? found.Declaration : null;
    }

    private void Merge(string filePath, ImportResolutionResult resolution, HashSet<string> merged, List<StormDiagnostic> diagnostics)
    {
        if(!merged.Add(filePath))
        {
            return;
        }

        var file = resolution.FilesByPath[filePath];
        foreach(var import in file.Imports)
        {
            var importedPath = StormImportResolver.ResolveImportPath(filePath, import.Path);
            if(!resolution.FilesByPath.ContainsKey(importedPath))
            {
                continue;
            }

            if(import.Alias != null)
            {
                Aliases.Add((import.Alias, importedPath));
            }
            else
            {
                Merge(importedPath, resolution, merged, diagnostics);
            }
        }

        foreach(var declaration in file.Declarations)
        {
            Declare(filePath, declaration, diagnostics);
            if(declaration is StormModeDecl mode)
            {
                foreach(var inner in mode.Declarations)
                {
                    Declare(filePath, inner, diagnostics);
                }
            }
        }
    }

    private void Declare(string filePath, StormDeclaration declaration, List<StormDiagnostic> diagnostics)
    {
        if(names.TryGetValue(declaration.Name, out var existing))
        {
            diagnostics.Add(new StormDiagnostic(
                StormDiagnosticIds.STORM_DUPLICATE_NAME,
                StormDiagnosticSeverity.Error,
                $"Duplicate declaration name '{declaration.Name}': first declared in '{existing.FilePath}' at {existing.Declaration.Line}:{existing.Declaration.Column}, redeclared in '{filePath}' at {declaration.Line}:{declaration.Column}",
                filePath,
                declaration.Line,
                declaration.Column));
            return;
        }

        names[declaration.Name] = (filePath, declaration);
        Declarations.Add((declaration.Name, filePath, declaration));
    }
}