namespace Storm.SourceGenerator.IR;

/// <summary>
/// Lowered representation of a token declaration.
/// Holds the token name, its lowered expression (with fragments inlined),
/// whether it is hidden, and its declaration-order index for tiebreaking.
/// </summary>
public sealed class TokenIR
{
    /// <summary>
    /// The token name as declared in the grammar (or a synthetic name
    /// for anonymous string literals materialized from rule expressions).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The lowered expression for this token, with all fragment references
    /// replaced by their inlined body expressions.
    /// </summary>
    public ExpressionIR Expression { get; }

    /// <summary>
    /// Whether this token was declared as <c>hidden token</c>.
    /// Synthetic tokens created for anonymous string literals in rules
    /// are also marked hidden.
    /// </summary>
    public bool IsHidden { get; }

    /// <summary>
    /// Zero-based declaration-order index used for maximal-munch tiebreaking.
    /// </summary>
    public int DeclarationIndex { get; }

    /// <summary>
    /// Whether this token was synthesized from an anonymous string literal
    /// in a rule expression (as opposed to being explicitly declared).
    /// </summary>
    public bool IsSynthetic { get; }

    /// <summary>
    /// The mode this token belongs to. Defaults to <c>"default"</c>.
    /// </summary>
    public string Mode { get; }

    /// <summary>
    /// The mode action (push/pop/none) for this token.
    /// Defaults to <see cref="NoModeActionIR"/>.
    /// </summary>
    public ModeActionIR ModeAction { get; }

    public TokenIR(string name, ExpressionIR expression, bool isHidden, int declarationIndex, bool isSynthetic = false, string mode = "default", ModeActionIR? modeAction = null)
    {
        Name = name;
        Expression = expression;
        IsHidden = isHidden;
        DeclarationIndex = declarationIndex;
        IsSynthetic = isSynthetic;
        Mode = mode;
        ModeAction = modeAction ?? new NoModeActionIR();
    }
}