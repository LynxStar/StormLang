using System.Collections.Generic;

namespace Storm.SourceGenerator.Emit;

/// <summary>
/// A single state in a DFA. Each state has a list of transitions
/// (on character ranges to target state IDs) and an optional accepting annotation.
/// </summary>
internal sealed class DfaState
{
    /// <summary>
    /// Unique identifier for this state within the DFA.
    /// </summary>
    public int Id { get; }

    /// <summary>
    /// Outgoing transitions from this state, stored as a sorted list of
    /// (CharRange, targetStateId) pairs. Sorted by CharRange.Lo for
    /// efficient lookup.
    /// </summary>
    public List<DfaTransition> Transitions { get; } = new List<DfaTransition>();

    /// <summary>
    /// -1 for non-accepting states. Otherwise, the token declaration index
    /// (token kind) that this state accepts.
    /// </summary>
    public int AcceptingTokenIndex { get; set; } = -1;

    public DfaState(int id)
    {
        Id = id;
    }
}