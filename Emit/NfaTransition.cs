namespace Storm.SourceGenerator.Emit;

/// <summary>
/// A transition from one NFA state to another, either on a set of
/// character ranges or on epsilon (ranges is null).
/// </summary>
internal sealed class NfaTransition
{
    /// <summary>
    /// Character ranges for this transition. Null for epsilon transitions.
    /// A character triggers this transition if it falls within any of the ranges.
    /// </summary>
    public CharRange[]? Ranges { get; }

    /// <summary>
    /// True if this is an epsilon transition (no input consumed).
    /// </summary>
    public bool IsEpsilon => Ranges == null;

    /// <summary>
    /// The target state of this transition.
    /// </summary>
    public NfaState Target { get; }

    /// <summary>
    /// Creates a character transition with the given ranges.
    /// </summary>
    public NfaTransition(CharRange[] ranges, NfaState target)
    {
        Ranges = ranges;
        Target = target;
    }

    /// <summary>
    /// Creates an epsilon transition to the given target state.
    /// </summary>
    public NfaTransition(NfaState target)
    {
        Ranges = null;
        Target = target;
    }
}