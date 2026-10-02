using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Storm.SourceGenerator.Analyze;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;
using Storm.SourceGenerator.Validate;

namespace Storm.SourceGenerator.Generator;

/// <summary>
/// Roslyn incremental source generator that processes *.storm grammar files
/// and emits C# source for the lexer, parser, AST node types, and runtime types.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class StormIncrementalGenerator : IIncrementalGenerator
{
    // Legacy AST shims have been retired. Keep the flag explicit for callers
    // that construct an EmissionContext directly.
    private const bool EmitObsoleteShims = false;

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Register for additional files matching *.storm
        var stormFiles = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".storm", StringComparison.OrdinalIgnoreCase));

        // Combine all storm files into a single collection
        var collectedFiles = stormFiles.Collect();

        // Register the source output
        var originType = context.AnalyzerConfigOptionsProvider.Select(
            static (options, _) => options.GlobalOptions.TryGetValue(
                "build_property.StormNodeOriginType", out var type) && !string.IsNullOrWhiteSpace(type)
                ? type : "object");

        context.RegisterSourceOutput(collectedFiles.Combine(originType),
            static (spc, input) => Execute(spc, input.Left, input.Right));
    }

    private static void Execute(
        SourceProductionContext context,
        System.Collections.Immutable.ImmutableArray<AdditionalText> stormTexts,
        string originType)
    {
        if(stormTexts.IsDefaultOrEmpty)
        {
            return;
        }

        // Build a file reader from the additional texts
        var fileContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach(var text in stormTexts)
        {
            var sourceText = text.GetText(context.CancellationToken);
            if(sourceText != null)
            {
                var normalizedPath = StormImportResolver.CanonicalizePath(text.Path);
                fileContents[normalizedPath] = sourceText.ToString();
            }
        }

        string? fileReader(string path)
        {
            var normalized = StormImportResolver.CanonicalizePath(path);
            return fileContents.TryGetValue(normalized, out var contents) ? contents : null;
        }

        // Parse all storm files to find the one with a grammar declaration
        var parsedFiles = new Dictionary<string, Frontend.StormFile>(StringComparer.OrdinalIgnoreCase);
        var parseDiagnostics = new List<StormDiagnostic>();

        foreach(var kv in fileContents)
        {
            var source = new Frontend.StormSourceText(kv.Key, kv.Value);
            var fileDiags = new List<StormDiagnostic>();
            var tokens = Frontend.StormLexer.Tokenize(source, fileDiags);
            var stormFile = Frontend.StormParser.Parse(tokens, source, fileDiags);
            parsedFiles[kv.Key] = stormFile;
            parseDiagnostics.AddRange(fileDiags);
        }

        // Find the root file (the one with a grammar declaration)
        string? rootPath = null;
        string? grammarName = null;
        Frontend.StormGrammarDecl? firstDecl = null;
        string? firstDeclPath = null;
        var hasMultipleRoots = false;

        foreach(var kv in parsedFiles)
        {
            if(kv.Value.GrammarDeclaration != null)
            {
                if(rootPath != null)
                {
                    // Multiple grammar declarations — report and bail
                    context.ReportDiagnostic(new StormDiagnostic(
                        StormDiagnosticIds.STORM_MULTIPLE_ROOT_GRAMMARS,
                        StormDiagnosticSeverity.Error,
                        $"Multiple files declare a `grammar` directive: first in '{firstDeclPath}' at {firstDecl!.Line}:{firstDecl.Column}, also in '{kv.Key}' at {kv.Value.GrammarDeclaration.Line}:{kv.Value.GrammarDeclaration.Column}",
                        kv.Key,
                        kv.Value.GrammarDeclaration.Line,
                        kv.Value.GrammarDeclaration.Column).ToRoslynDiagnostic());
                    hasMultipleRoots = true;
                }
                else
                {
                    rootPath = kv.Key;
                    grammarName = kv.Value.GrammarDeclaration.Name;
                    firstDecl = kv.Value.GrammarDeclaration;
                    firstDeclPath = kv.Key;
                }
            }
        }

        if(hasMultipleRoots)
        {
            return;
        }

        if(rootPath == null || grammarName == null)
        {
            // No grammar declaration found
            context.ReportDiagnostic(new StormDiagnostic(
                StormDiagnosticIds.STORM_NO_ROOT_GRAMMAR,
                StormDiagnosticSeverity.Error,
                "No file declares a `grammar` directive",
                stormTexts[0].Path,
                1,
                1).ToRoslynDiagnostic());
            return;
        }

        var consumerNamespace = grammarName + ".Generated";

        // Step 1: Parse and resolve imports
        var resolver = new StormImportResolver(fileReader);
        var resolution = resolver.Resolve(rootPath);

        // Report parse/import diagnostics
        foreach(var diag in resolution.Diagnostics)
        {
            context.ReportDiagnostic(diag.ToRoslynDiagnostic());
        }

        if(!resolution.Succeeded)
        {
            return;
        }

        // Step 2: Build effective grammar
        var grammar = StormGrammar.Build(resolution);

        foreach(var diag in grammar.Diagnostics)
        {
            context.ReportDiagnostic(diag.ToRoslynDiagnostic());
        }

        if(!grammar.Succeeded)
        {
            return;
        }

        // Step 3: Validate
        var validationDiagnostics = RunValidation(grammar);
        var hasValidationErrors = false;

        foreach(var diag in validationDiagnostics)
        {
            context.ReportDiagnostic(diag.ToRoslynDiagnostic());
            if(diag.Severity == StormDiagnosticSeverity.Error)
            {
                hasValidationErrors = true;
            }
        }

        if(hasValidationErrors)
        {
            return;
        }

        // Step 3b: Trait validation
        var traitValidationDiags = TraitValidationPass.Run(grammar);
        foreach(var diag in traitValidationDiags)
        {
            context.ReportDiagnostic(diag.ToRoslynDiagnostic());
            if(diag.Severity == StormDiagnosticSeverity.Error)
            {
                hasValidationErrors = true;
            }
        }

        if(hasValidationErrors)
        {
            return;
        }

        // Step 4: Lower to IR
        var loweringResult = GrammarIRLowering.Lower(grammar);

        foreach(var diag in loweringResult.Diagnostics)
        {
            context.ReportDiagnostic(diag.ToRoslynDiagnostic());
        }

        if(!loweringResult.Succeeded || loweringResult.Grammar == null)
        {
            return;
        }

        // Step 4b: Build trait IR
        var traitBuildResult = TraitIRBuilder.Build(grammar);

        // Step 5: Analyze for selective packrat memoization (pure, deterministic).
        // Runs after lowering succeeds and before emission, operating on the same
        // lowered GrammarIR the emitters consume. Cannot fail on validated IR, so
        // it emits no diagnostics. Returns MemoizationPlan.Empty when nothing qualifies.
        var memoizationPlan = MemoizationAnalysisPass.Run(loweringResult.Grammar);

        // Step 6: Emit.
        // EmitObsoleteShims is threaded in explicitly here as the single migration control
        // point (see the constant on this class); Task 27 flips it to false to drop the shims.
        var emissionContext = new EmissionContext(
            consumerNamespace, grammarName, loweringResult.Grammar, memoizationPlan,
            traitBuildResult.Traits, traitBuildResult.RuleTraitBindings,
            emitObsoleteShims: EmitObsoleteShims);

        // Emit runtime types
        var runtimeSource = AstEmitter.EmitRuntimeTypes(originType);
        context.AddSource("Storm.Runtime.g.cs", runtimeSource);

        // Emit AST node types
        var astSource = AstEmitter.EmitAstNodes(emissionContext);
        context.AddSource($"{grammarName}.Ast.g.cs", astSource);

        // Emit lexer
        var lexerSource = LexerEmitter.Emit(emissionContext);
        context.AddSource($"{grammarName}.Lexer.g.cs", lexerSource);

        // Emit parser
        var parserSource = ParserEmitter.Emit(emissionContext);
        context.AddSource($"{grammarName}.Parser.g.cs", parserSource);
    }

    /// <summary>
    /// Runs all validation passes on the effective grammar.
    /// </summary>
    private static List<StormDiagnostic> RunValidation(StormGrammar grammar)
    {
        var diagnostics = new List<StormDiagnostic>();

        // Pass 1: Name resolution
        var nameResolutionDiags = NameResolutionPass.Run(grammar);
        diagnostics.AddRange(nameResolutionDiags);
        if(nameResolutionDiags.Any(d => d.Severity == StormDiagnosticSeverity.Error))
        {
            return diagnostics; // Later passes depend on name resolution
        }

        // Pass 2: Mode validation
        var modeValidationDiags = ModeValidationPass.Run(grammar);
        diagnostics.AddRange(modeValidationDiags);
        if(modeValidationDiags.Any(d => d.Severity == StormDiagnosticSeverity.Error))
        {
            return diagnostics;
        }

        // Pass 3: Left-recursion check
        var leftRecDiags = LeftRecursionPass.Run(grammar);
        diagnostics.AddRange(leftRecDiags);
        if(leftRecDiags.Any(d => d.Severity == StormDiagnosticSeverity.Error))
        {
            return diagnostics;
        }

        // Pass 4: Inline prefix verification
        var inlinePrefixDiags = InlinePrefixVerification.Verify(grammar);
        diagnostics.AddRange(inlinePrefixDiags);
        if(inlinePrefixDiags.Any(d => d.Severity == StormDiagnosticSeverity.Error))
        {
            return diagnostics;
        }

        // Pass 5: Reachability check
        var reachDiags = ReachabilityPass.Run(grammar);
        diagnostics.AddRange(reachDiags);

        return diagnostics;
    }
}