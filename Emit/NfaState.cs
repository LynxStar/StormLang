using System.Collections.Generic;

namespace Storm.SourceGenerator.Emit;

/// <summary>
/// A single state in an NFA. Each state has a list of transitions
/// (on a character range or epsilon) and an optional accepting annotation.
/// </summary>
internal sealed class NfaState
{
    /// <summary>
    /// Unique identifier for this state within the NFA.
    /// </summary>
    public int Id { get; }

    /// <summary>
    /// Outgoing transitions from this state.
    /// </summary>
    public List<NfaTransition> Transitions { get; } = new List<NfaTransition>();

    /// <summary>
    /// If this is an accepting state, the token kind (declaration index).
    /// -1 means non-accepting.
    /// </summary>
    public int AcceptingTokenIndex { get; set; } = -1;

    public NfaState(int id)
    {
        Id = id;
    }
}