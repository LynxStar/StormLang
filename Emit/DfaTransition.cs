namespace Storm.SourceGenerator.Emit;

/// <summary>
/// A single DFA transition on a character range to a target state.
/// </summary>
internal sealed class DfaTransition
{
    /// <summary>
    /// The character range that triggers this transition.
    /// </summary>
    public CharRange Range { get; }

    /// <summary>
    /// The ID of the target DFA state.
    /// </summary>
    public int TargetStateId { get; }

    public DfaTransition(CharRange range, int targetStateId)
    {
        Range = range;
        TargetStateId = targetStateId;
    }
}