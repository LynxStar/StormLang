namespace Storm.SourceGenerator.Emit;

/// <summary>
/// An NFA fragment with a single start state and a single accept state.
/// Thompson's construction always produces fragments of this shape.
/// </summary>
internal sealed class NfaFragment
{
    /// <summary>
    /// The entry state of this fragment.
    /// </summary>
    public NfaState Start { get; }

    /// <summary>
    /// The accepting state of this fragment.
    /// </summary>
    public NfaState Accept { get; }

    public NfaFragment(NfaState start, NfaState accept)
    {
        Start = start;
        Accept = accept;
    }
}