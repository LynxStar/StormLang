using System.Collections.Generic;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Validate;

/// <summary>
/// Validates mode-related declarations in the effective grammar.
/// Checks for duplicate mode names, rules inside mode blocks, undefined push targets,
/// pop from default mode, and reserved mode names.
/// </summary>
public static class ModeValidationPass
{
    public static List<StormDiagnostic> Run(StormGrammar grammar)
    {
        var diagnostics = new List<StormDiagnostic>();

        // Collect all mode declarations and build the set of declared mode names.
        // "default" is always implicitly in the mode list.
        var modeDecls = new List<StormModeDecl>();
        var declaredModeNames = new HashSet<string> { "default" };

        foreach(var decl in grammar.Declarations)
        {
            if(decl is StormModeDecl modeDecl)
            {
                modeDecls.Add(modeDecl);
                _ = declaredModeNames.Add(modeDecl.Name);
            }
        }

        ValidateDuplicateModeNames(modeDecls, diagnostics);
        ValidateReservedModeName(modeDecls, diagnostics);
        ValidateRulesInModeBlocks(modeDecls, diagnostics);
        ValidatePushTargets(grammar.Declarations, modeDecls, declaredModeNames, diagnostics);
        ValidatePopFromDefault(grammar.Declarations, modeDecls, diagnostics);

        return diagnostics;
    }

    /// <summary>
    /// Reports STORM0011 if two mode declarations share the same name.
    /// </summary>
    private static void ValidateDuplicateModeNames(
        List<StormModeDecl> modeDecls,
        List<StormDiagnostic> diagnostics)
    {
        var seen = new Dictionary<string, StormModeDecl>();

        foreach(var modeDecl in modeDecls)
        {
            if(seen.TryGetValue(modeDecl.Name, out var first))
            {
                diagnostics.Add(new StormDiagnostic(
                    StormDiagnosticIds.STORM_DUPLICATE_MODE,
                    StormDiagnosticSeverity.Error,
                    $"Duplicate mode name '{modeDecl.Name}': first declared at {first.Line}:{first.Column}",
                    null,
                    modeDecl.Line,
                    modeDecl.Column));
            }
            else
            {
                seen[modeDecl.Name] = modeDecl;
            }
        }
    }

    /// <summary>
    /// Reports STORM0015 if a mode declaration uses the reserved name "default".
    /// </summary>
    private static void ValidateReservedModeName(
        List<StormModeDecl> modeDecls,
        List<StormDiagnostic> diagnostics)
    {
        foreach(var modeDecl in modeDecls)
        {
            if(modeDecl.Name == "default")
            {
                diagnostics.Add(new StormDiagnostic(
                    StormDiagnosticIds.STORM_RESERVED_MODE_NAME,
                    StormDiagnosticSeverity.Error,
                    $"'{modeDecl.Name}' is a reserved mode name and cannot be used as a user-defined mode",
                    null,
                    modeDecl.Line,
                    modeDecl.Column));
            }
        }
    }

    /// <summary>
    /// Reports STORM0012 if a mode block contains any rule declarations.
    /// </summary>
    private static void ValidateRulesInModeBlocks(
        List<StormModeDecl> modeDecls,
        List<StormDiagnostic> diagnostics)
    {
        foreach(var modeDecl in modeDecls)
        {
            foreach(var innerDecl in modeDecl.Declarations)
            {
                if(innerDecl is StormRuleDecl)
                {
                    diagnostics.Add(new StormDiagnostic(
                        StormDiagnosticIds.STORM_RULE_IN_MODE,
                        StormDiagnosticSeverity.Error,
                        "Parser rules are not permitted inside mode blocks",
                        null,
                        innerDecl.Line,
                        innerDecl.Column));
                }
            }
        }
    }

    /// <summary>
    /// Reports STORM0013 if a push action references a mode name not in the declared mode set.
    /// Checks both top-level declarations and declarations inside mode blocks.
    /// </summary>
    private static void ValidatePushTargets(
        IReadOnlyList<StormDeclaration> topLevelDecls,
        List<StormModeDecl> modeDecls,
        HashSet<string> declaredModeNames,
        List<StormDiagnostic> diagnostics)
    {
        // Check top-level token declarations
        foreach(var decl in topLevelDecls)
        {
            CheckPushAction(decl, declaredModeNames, diagnostics);
        }

        // Check declarations inside mode blocks
        foreach(var modeDecl in modeDecls)
        {
            foreach(var innerDecl in modeDecl.Declarations)
            {
                CheckPushAction(innerDecl, declaredModeNames, diagnostics);
            }
        }
    }

    /// <summary>
    /// Checks a single declaration for a push action referencing an undefined mode.
    /// </summary>
    private static void CheckPushAction(
        StormDeclaration decl,
        HashSet<string> declaredModeNames,
        List<StormDiagnostic> diagnostics)
    {
        var action = GetModeAction(decl);
        if(action is StormPushAction pushAction && !declaredModeNames.Contains(pushAction.ModeName))
        {
            diagnostics.Add(new StormDiagnostic(
                StormDiagnosticIds.STORM_UNDEFINED_MODE,
                StormDiagnosticSeverity.Error,
                $"Push action references undefined mode '{pushAction.ModeName}'",
                null,
                decl.Line,
                decl.Column));
        }
    }

    /// <summary>
    /// Reports STORM0014 (warning) if a pop action is on a token in the default mode
    /// and no other mode pushes to default. If another mode pushes to default, the pop
    /// is meaningful — it returns the lexer to the mode that pushed.
    /// </summary>
    private static void ValidatePopFromDefault(
        IReadOnlyList<StormDeclaration> topLevelDecls,
        List<StormModeDecl> modeDecls,
        List<StormDiagnostic> diagnostics)
    {
        // Check whether any token in any mode pushes to "default".
        // If so, a pop from default is valid — it returns to the pushing mode.
        var anyPushToDefault = false;
        foreach(var modeDecl in modeDecls)
        {
            foreach(var innerDecl in modeDecl.Declarations)
            {
                var action = GetModeAction(innerDecl);
                if(action is StormPushAction pushAction && pushAction.ModeName == "default")
                {
                    anyPushToDefault = true;
                    break;
                }
            }

            if(anyPushToDefault)
            {
                break;
            }
        }

        if(anyPushToDefault)
        {
            // Pop from default is valid — another mode pushes to default.
            return;
        }

        // Only top-level declarations are in the default mode.
        // Declarations inside StormModeDecl are in their enclosing mode.
        foreach(var decl in topLevelDecls)
        {
            // Skip mode declarations themselves — they aren't tokens
            if(decl is StormModeDecl)
            {
                continue;
            }

            var action = GetModeAction(decl);
            if(action is StormPopAction)
            {
                diagnostics.Add(new StormDiagnostic(
                    StormDiagnosticIds.STORM_POP_FROM_DEFAULT,
                    StormDiagnosticSeverity.Warning,
                    "Pop action on default-mode token has no effect",
                    null,
                    decl.Line,
                    decl.Column));
            }
        }
    }

    /// <summary>
    /// Extracts the mode action from a declaration, if it has one.
    /// </summary>
    private static StormModeAction? GetModeAction(StormDeclaration decl)
    {
        return decl switch
        {
            StormTokenDecl tokenDecl => tokenDecl.Action,
            StormHiddenTokenDecl hiddenTokenDecl => hiddenTokenDecl.Action,
            _ => null,
        };
    }
}