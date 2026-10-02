using System;
using System.Collections.Generic;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;

namespace Storm.SourceGenerator.Resolve;

/// <summary>
/// Represents the effective grammar produced by merging a root Storm file
/// with all its resolved imports. Enforces duplicate-name detection for
/// unnamespaced declarations and establishes the effective declaration order
/// for token tiebreaking (R6.8).
/// </summary>
public sealed class StormGrammar
{
    /// <summary>
    /// All declarations in effective order:
    /// unnamespaced imports first (in import-appearance order),
    /// then root declarations, then namespaced-only declarations last (R6.8).
    /// This ensures imported keyword tokens precede IDENTIFIER for
    /// first-match-wins tiebreaking.
    /// </summary>
    public IReadOnlyList<StormDeclaration> Declarations { get; }

    /// <summary>
    /// Qualified name → declaration for namespaced imports.
    /// Keys are of the form "Namespace.Name".
    /// </summary>
    public IReadOnlyDictionary<string, StormDeclaration> NamespacedDeclarations { get; }

    /// <summary>
    /// Diagnostics produced during grammar construction (e.g. duplicate names).
    /// </summary>
    public IReadOnlyList<StormDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Whether grammar construction completed without errors.
    /// </summary>
    public bool Succeeded { get; }

    /// <summary>
    /// The grammar name from the <c>grammar</c> declaration in the root file,
    /// or null if no grammar declaration was found.
    /// </summary>
    public string? GrammarName { get; }

    /// <summary>
    /// All declared mode names, always including <c>"default"</c>.
    /// Extracted from <see cref="StormModeDecl"/> nodes in the declarations list.
    /// </summary>
    public IReadOnlyList<string> ModeNames { get; }

    private Dictionary<string, StormDeclaration> declarationsByName;

    private StormGrammar(
        IReadOnlyList<StormDeclaration> declarations,
        IReadOnlyDictionary<string, StormDeclaration> namespacedDeclarations,
        Dictionary<string, StormDeclaration> declarationsByName,
        IReadOnlyList<StormDiagnostic> diagnostics,
        bool succeeded,
        string? grammarName,
        IReadOnlyList<string> modeNames)
    {
        Declarations = declarations;
        NamespacedDeclarations = namespacedDeclarations;
        this.declarationsByName = declarationsByName;
        Diagnostics = diagnostics;
        Succeeded = succeeded;
        GrammarName = grammarName;
        ModeNames = modeNames;
    }

    /// <summary>
    /// Looks up a declaration by name. Supports both unqualified names
    /// (searched in the unnamespaced declaration set) and qualified names
    /// of the form "Namespace.Name" (searched in the namespaced declaration set).
    /// </summary>
    /// <param name="name">The unqualified or qualified name to look up.</param>
    /// <returns>The declaration if found; otherwise null.</returns>
    public StormDeclaration? LookupByName(string name)
    {
        if(name == null)
        {
            throw new ArgumentNullException(nameof(name));
        }
        // Try unqualified lookup first
        if(declarationsByName.TryGetValue(name, out var decl))
        {
            return decl;
        }

        // Try qualified lookup
        if(NamespacedDeclarations is Dictionary<string, StormDeclaration> nsDict)
        {
            if(nsDict.TryGetValue(name, out var nsDecl))
            {
                return nsDecl;
            }
        }
        else
        {
            // Fallback for IReadOnlyDictionary
            if(NamespacedDeclarations.TryGetValue(name, out var nsDecl))
            {
                return nsDecl;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds a <see cref="StormGrammar"/> from an <see cref="ImportResolutionResult"/>.
    /// </summary>
    public static StormGrammar Build(ImportResolutionResult resolution)
    {
        if(resolution == null)
        {
            throw new ArgumentNullException(nameof(resolution));
        }

        var diagnostics = new List<StormDiagnostic>();
        var declarations = new List<StormDeclaration>();
        var namespacedDeclarations = new Dictionary<string, StormDeclaration>(StringComparer.Ordinal);
        var declarationsByName = new Dictionary<string, StormDeclaration>(StringComparer.Ordinal);

        // Track where each unnamespaced declaration name was first defined (for duplicate detection)
        var nameToSourceLocation = new Dictionary<string, (string FilePath, StormDeclaration Decl)>(StringComparer.Ordinal);

        var failed = false;

        // Scan for grammar declarations across all files
        string? grammarName = null;
        string? grammarDeclFilePath = null;
        StormGrammarDecl? firstGrammarDecl = null;

        foreach(var kv in resolution.OrderedFiles)
        {
            var file = kv.Value;
            if(file.GrammarDeclaration != null)
            {
                if(grammarName != null)
                {
                    // Multiple grammar declarations
                    diagnostics.Add(new StormDiagnostic(
                        StormDiagnosticIds.STORM_MULTIPLE_ROOT_GRAMMARS,
                        StormDiagnosticSeverity.Error,
                        $"Multiple files declare a `grammar` directive: first in '{grammarDeclFilePath}' at {firstGrammarDecl!.Line}:{firstGrammarDecl.Column}, also in '{kv.Key}' at {file.GrammarDeclaration.Line}:{file.GrammarDeclaration.Column}",
                        kv.Key,
                        file.GrammarDeclaration.Line,
                        file.GrammarDeclaration.Column));
                    failed = true;
                }
                else
                {
                    grammarName = file.GrammarDeclaration.Name;
                    grammarDeclFilePath = kv.Key;
                    firstGrammarDecl = file.GrammarDeclaration;
                }
            }
        }

        if(grammarName == null)
        {
            diagnostics.Add(new StormDiagnostic(
                StormDiagnosticIds.STORM_NO_ROOT_GRAMMAR,
                StormDiagnosticSeverity.Error,
                "No file declares a `grammar` directive",
                resolution.RootPath,
                1,
                1));
            failed = true;
        }

        // Track which files contribute only via namespaced imports
        // We need to know which files are imported with a namespace alias
        // so we can separate namespaced-only declarations

        // First pass: determine which files are imported with namespace aliases
        // and which are imported without (unnamespaced)
        var rootPath = resolution.RootPath;
        var rootFile = resolution.FilesByPath[rootPath];

        // We need to walk the import tree to determine:
        // 1. Which files contribute unnamespaced declarations (root + unnamespaced imports, recursively)
        // 2. Which files contribute namespaced declarations (namespaced imports)
        // 3. The order of contributions

        // Collect namespaced declarations from the root file's namespaced imports
        // and unnamespaced declarations from root + unnamespaced imports in DFS pre-order

        var visitedForMerge = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var namespacedFiles = new List<(string Alias, string Path, StormFile File)>();

        // Process root file and its unnamespaced imports recursively
        CollectDeclarations(
            rootPath,
            rootFile,
            resolution,
            visitedForMerge,
            declarations,
            namespacedFiles,
            declarationsByName,
            nameToSourceLocation,
            diagnostics,
            ref failed);

        // Now add namespaced declarations (R6.8: namespaced-only declarations last)
        var namespacedDeclsForOrder = new List<StormDeclaration>();
        foreach(var (alias, path, file) in namespacedFiles)
        {
            foreach(var decl in file.Declarations)
            {
                var qualifiedName = alias + "." + decl.Name;
                if(namespacedDeclarations.ContainsKey(qualifiedName))
                {
                    // Duplicate qualified name — this is an error
                    var existing = namespacedDeclarations[qualifiedName];
                    diagnostics.Add(new StormDiagnostic(
                        StormDiagnosticIds.STORM_DUPLICATE_NAME,
                        StormDiagnosticSeverity.Error,
                        $"Duplicate qualified declaration name '{qualifiedName}': first declared at {existing.Line}:{existing.Column}, redeclared at {decl.Line}:{decl.Column} in '{path}'",
                        path,
                        decl.Line,
                        decl.Column));
                    failed = true;
                }
                else
                {
                    namespacedDeclarations[qualifiedName] = decl;
                    namespacedDeclsForOrder.Add(decl);
                }
            }
        }

        // Append namespaced declarations to the effective order (R6.8)
        declarations.AddRange(namespacedDeclsForOrder);

        // Build mode names list: always include "default", plus any StormModeDecl names
        var modeNames = new List<string> { "default" };
        foreach(var decl in declarations)
        {
            if(decl is StormModeDecl modeDecl && modeDecl.Name != "default")
            {
                modeNames.Add(modeDecl.Name);
            }
        }

        return new StormGrammar(
            declarations,
            namespacedDeclarations,
            declarationsByName,
            diagnostics,
            !failed,
            grammarName,
            modeNames);
    }

    /// <summary>
    /// Recursively collects declarations from a file and its unnamespaced imports in DFS pre-order.
    /// Namespaced imports are collected separately for later processing.
    /// </summary>
    private static void CollectDeclarations(
        string filePath,
        StormFile file,
        ImportResolutionResult resolution,
        HashSet<string> visitedForMerge,
        List<StormDeclaration> declarations,
        List<(string Alias, string Path, StormFile File)> namespacedFiles,
        Dictionary<string, StormDeclaration> declarationsByName,
        Dictionary<string, (string FilePath, StormDeclaration Decl)> nameToSourceLocation,
        List<StormDiagnostic> diagnostics,
        ref bool failed)
    {
        if(!visitedForMerge.Add(filePath))
        {
            return;
        }

        // Process unnamespaced imports first (they appear before declarations
        // in the source), then the file's own declarations. This ensures that
        // imported keyword tokens precede IDENTIFIER in declaration order,
        // giving keywords priority via first-match-wins tiebreaking (R6.8).
        foreach(var import in file.Imports)
        {
            var resolvedPath = ResolveImportPath(filePath, import.Path);

            if(!resolution.FilesByPath.ContainsKey(resolvedPath))
            {
                // File wasn't resolved — diagnostic already emitted by StormImportResolver
                continue;
            }

            var importedFile = resolution.FilesByPath[resolvedPath];

            if(import.Alias != null)
            {
                // Namespaced import — collect for later processing
                namespacedFiles.Add((import.Alias, resolvedPath, importedFile));
            }
            else
            {
                // Unnamespaced import — recursively merge declarations
                CollectDeclarations(
                    resolvedPath,
                    importedFile,
                    resolution,
                    visitedForMerge,
                    declarations,
                    namespacedFiles,
                    declarationsByName,
                    nameToSourceLocation,
                    diagnostics,
                    ref failed);
            }
        }

        // Now process this file's own declarations.
        foreach(var decl in file.Declarations)
        {
            // Handle mode declarations: add the mode itself and each inner declaration
            if(decl is StormModeDecl modeDecl)
            {
                // Add the mode declaration itself (for mode name tracking)
                if(nameToSourceLocation.TryGetValue(modeDecl.Name, out var existingMode))
                {
                    diagnostics.Add(new StormDiagnostic(
                        StormDiagnosticIds.STORM_DUPLICATE_NAME,
                        StormDiagnosticSeverity.Error,
                        $"Duplicate declaration name '{modeDecl.Name}': first declared in '{existingMode.FilePath}' at {existingMode.Decl.Line}:{existingMode.Decl.Column}, redeclared in '{filePath}' at {modeDecl.Line}:{modeDecl.Column}",
                        filePath,
                        modeDecl.Line,
                        modeDecl.Column));
                    failed = true;
                }
                else
                {
                    nameToSourceLocation[modeDecl.Name] = (filePath, modeDecl);
                    declarationsByName[modeDecl.Name] = modeDecl;
                    declarations.Add(modeDecl);
                }

                // Add each inner declaration with its source position preserved
                foreach(var innerDecl in modeDecl.Declarations)
                {
                    if(nameToSourceLocation.TryGetValue(innerDecl.Name, out var existingInner))
                    {
                        diagnostics.Add(new StormDiagnostic(
                            StormDiagnosticIds.STORM_DUPLICATE_NAME,
                            StormDiagnosticSeverity.Error,
                            $"Duplicate declaration name '{innerDecl.Name}': first declared in '{existingInner.FilePath}' at {existingInner.Decl.Line}:{existingInner.Decl.Column}, redeclared in '{filePath}' at {innerDecl.Line}:{innerDecl.Column}",
                            filePath,
                            innerDecl.Line,
                            innerDecl.Column));
                        failed = true;
                    }
                    else
                    {
                        nameToSourceLocation[innerDecl.Name] = (filePath, innerDecl);
                        declarationsByName[innerDecl.Name] = innerDecl;
                        declarations.Add(innerDecl);
                    }
                }
            }
            else
            {
                if(nameToSourceLocation.TryGetValue(decl.Name, out var existing))
                {
                    diagnostics.Add(new StormDiagnostic(
                        StormDiagnosticIds.STORM_DUPLICATE_NAME,
                        StormDiagnosticSeverity.Error,
                        $"Duplicate declaration name '{decl.Name}': first declared in '{existing.FilePath}' at {existing.Decl.Line}:{existing.Decl.Column}, redeclared in '{filePath}' at {decl.Line}:{decl.Column}",
                        filePath,
                        decl.Line,
                        decl.Column));
                    failed = true;
                }
                else
                {
                    nameToSourceLocation[decl.Name] = (filePath, decl);
                    declarationsByName[decl.Name] = decl;
                    declarations.Add(decl);
                }
            }
        }
    }

    /// <summary>
    /// Resolves an import path relative to the importing file's directory.
    /// </summary>
    private static string ResolveImportPath(string importingFilePath, string importPath)
    {
        var importingDir = GetDirectoryName(importingFilePath);
        var combined = CombinePath(importingDir, importPath);
        return StormImportResolver.CanonicalizePath(combined);
    }

    private static string GetDirectoryName(string path)
    {
        path = path.Replace('\\', '/');
        var lastSlash = path.LastIndexOf('/');
        return lastSlash >= 0
            ? path.Substring(0, lastSlash)
            : "."
            ;
    }

    private static string CombinePath(string directory, string relativePath)
    {
        return string.IsNullOrEmpty(directory) || directory == "."
            ? relativePath
            : directory + "/" + relativePath
            ;
    }
}