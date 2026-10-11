using System;
using System.Collections.Generic;
using System.Linq;
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
    /// first-match-wins tiebreaking. Each appears once, a namespaced-only one
    /// under its qualified name, and every name a declaration uses is the
    /// effective name of what it means in the declaring file.
    /// </summary>
    public IReadOnlyList<StormDeclaration> Declarations { get; }

    /// <summary>
    /// Qualified name → declaration for namespaced imports.
    /// Keys are of the form "Namespace.Name", or "Outer.Inner.Name" for a
    /// namespace imported by a namespaced file, one per alias that reaches it.
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

        var naming = EffectiveNames.Assign(resolution);
        var unresolved = new List<StormDiagnostic>();
        var rewritten = new Dictionary<StormDeclaration, StormDeclaration>(DeclarationIdentity.Instance);
        foreach(var (declaration, scope, filePath) in naming.Owners.Where(owner => owner.Declaration is not StormModeDecl))
        {
            var rewriter = ReferenceEquals(scope, naming.Root)
                ? new DeclarationRewriter(scope, naming.ScopeOf, naming.Names, null)
                : new DeclarationRewriter(naming.ScopeOf(filePath), naming.ScopeOf, naming.Names, unresolved);
            rewritten[declaration] = rewriter.Rewrite(declaration, naming.Names[declaration], filePath);
        }

        foreach(var mode in naming.Owners.Select(owner => owner.Declaration).OfType<StormModeDecl>())
        {
            var inner = mode.Declarations.Select(declaration => rewritten.TryGetValue(declaration, out var renamed) ? renamed : declaration).ToList();
            rewritten[mode] = naming.Names[mode] == mode.Name && inner.Zip(mode.Declarations, ReferenceEquals).All(same => same)
                ? mode
                : mode with { Name = naming.Names[mode], Body = new StormReferenceExpr(naming.Names[mode]), Declarations = inner };
        }

        diagnostics.AddRange(naming.Diagnostics);
        diagnostics.AddRange(unresolved);
        failed |= diagnostics.Any(diagnostic => diagnostic.Severity == StormDiagnosticSeverity.Error);

        var declarations = naming.Owners.Select(owner => rewritten[owner.Declaration]).ToList();
        var declarationsByName = naming.Root.Declarations.ToDictionary(declared => declared.Name, declared => rewritten[declared.Declaration], StringComparer.Ordinal);
        var namespacedDeclarations = naming.Spellings.ToDictionary(spelling => spelling.Key, spelling => rewritten[spelling.Value], StringComparer.Ordinal);

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
}