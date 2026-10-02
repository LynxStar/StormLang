using System.Collections.Generic;

namespace Storm.SourceGenerator.Emit;

/// <summary>
/// A complete DFA with a start state and a list of all states.
/// Produced by subset construction from a combined NFA.
/// </summary>
internal sealed class Dfa
{
    /// <summary>
    /// The ID of the start state.
    /// </summary>
    public int StartStateId { get; }

    /// <summary>
    /// All states in the DFA, indexed by their ID.
    /// </summary>
    public IReadOnlyList<DfaState> States { get; }

    public Dfa(int startStateId, IReadOnlyList<DfaState> states)
    {
        StartStateId = startStateId;
        States = states;
    }
}