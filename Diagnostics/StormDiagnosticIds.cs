namespace Storm.SourceGenerator.Diagnostics;

/// <summary>
/// Stable diagnostic identifiers for all Storm source-generator diagnostics.
/// Each property is used as the <see cref="StormDiagnostic.Id"/> value and maps
/// to a Roslyn <c>DiagnosticDescriptor</c> when reported at build time.
/// </summary>
public static class StormDiagnosticIds
{
    /// <summary>A Storm grammar file contains a syntax error.</summary>
    public static string STORM_SYNTAX_ERROR { get; } = "STORM0001";

    /// <summary>An import path could not be resolved.</summary>
    public static string STORM_IMPORT_NOT_FOUND { get; } = "STORM0002";

    /// <summary>Two unnamespaced declarations share the same name.</summary>
    public static string STORM_DUPLICATE_NAME { get; } = "STORM0003";

    /// <summary>A reference to a rule, token, or fragment could not be resolved.</summary>
    public static string STORM_UNDEFINED_REFERENCE { get; } = "STORM0004";

    /// <summary>Direct or indirect left-recursion that cannot be lowered to LL(*).</summary>
    public static string STORM_LEFT_RECURSION { get; } = "STORM0005";

    /// <summary>An alternative is statically unreachable under PEG ordered-choice semantics.</summary>
    public static string STORM_UNREACHABLE_ALTERNATIVE { get; } = "STORM0006";

    /// <summary>A grammar construct that the source generator cannot lower to C#.</summary>
    public static string STORM_UNSUPPORTED_CONSTRUCT { get; } = "STORM0008";

    /// <summary>No file declares a <c>grammar</c> directive.</summary>
    public static string STORM_NO_ROOT_GRAMMAR { get; } = "STORM0009";

    /// <summary>Multiple files declare a <c>grammar</c> directive.</summary>
    public static string STORM_MULTIPLE_ROOT_GRAMMARS { get; } = "STORM0010";

    /// <summary>Two mode declarations share the same name.</summary>
    public static string STORM_DUPLICATE_MODE { get; } = "STORM0011";

    /// <summary>A parser rule declaration appears inside a mode block, which is not permitted.</summary>
    public static string STORM_RULE_IN_MODE { get; } = "STORM0012";

    /// <summary>A push action references a mode name that is not declared in the grammar.</summary>
    public static string STORM_UNDEFINED_MODE { get; } = "STORM0013";

    /// <summary>A pop action on a default-mode token has no effect because there is no parent mode to return to.</summary>
    public static string STORM_POP_FROM_DEFAULT { get; } = "STORM0014";

    /// <summary>A mode declaration uses the reserved name <c>default</c>, which represents the implicit top-level mode.</summary>
    public static string STORM_RESERVED_MODE_NAME { get; } = "STORM0015";

    /// <summary>A <c>@memo</c>-annotated rule was excluded from the memoization plan because it is not speculative-safe.</summary>
    public static string STORM_MEMO_IGNORED { get; } = "STORM0016";

    /// <summary>An inline block arm's target rule does not begin with a reference to the declared prefix rule.</summary>
    public static string STORM_INLINE_PREFIX_MISMATCH { get; } = "STORM0017";

    /// <summary>Trait field type '{typeName}' in trait '{traitName}' cannot be resolved to a token or rule.</summary>
    public static string STORM_TRAIT_FIELD_UNRESOLVED { get; } = "STORM0018";

    /// <summary>Trait binding '{traitName}' on rule '{ruleName}' cannot be resolved to a declared trait.</summary>
    public static string STORM_TRAIT_BINDING_UNRESOLVED { get; } = "STORM0019";

    /// <summary>Rule '{ruleName}' binds trait '{traitName}' but is missing field '{fieldName}'.</summary>
    public static string STORM_TRAIT_FIELD_MISSING { get; } = "STORM0020";

    /// <summary>Rule '{ruleName}' field '{fieldName}' has type '{actual}' but trait '{traitName}' requires '{expected}'.</summary>
    public static string STORM_TRAIT_FIELD_MISMATCH { get; } = "STORM0021";

    /// <summary>Rule '{ruleName}' has named alternatives and cannot bind traits.</summary>
    public static string STORM_TRAIT_MULTI_ALT { get; } = "STORM0022";

    /// <summary>Trait '{traitName}' composes '{composedName}' which is not a declared trait.</summary>
    public static string STORM_TRAIT_COMPOSED_UNRESOLVED { get; } = "STORM0023";

    /// <summary>Inline arm target rule '{targetName}' does not satisfy trait '{traitName}' bound on rule '{ruleName}'.</summary>
    public static string STORM_TRAIT_INLINE_ARM_UNSATISFIED { get; } = "STORM0024";

    /// <summary>Trait '{traitName}' has conflicting definitions for field '{fieldName}' from composed traits.</summary>
    public static string STORM_TRAIT_FIELD_CONFLICT { get; } = "STORM0025";
}