using System.Collections.Generic;

namespace Storm.SourceGenerator.Emit;

/// <summary>
/// A complete NFA with a start state and a list of all reachable states.
/// Produced by combining individual token NFA fragments into a single automaton.
/// </summary>
internal sealed class Nfa
{
    /// <summary>
    /// The start state of the NFA.
    /// </summary>
    public NfaState StartState { get; }

    /// <summary>
    /// All states reachable from the start state.
    /// </summary>
    public IReadOnlyList<NfaState> States { get; }

    public Nfa(NfaState startState)
    {
        StartState = startState;
        States = CollectStates(startState);
    }

    /// <summary>
    /// Collects all states reachable from the given start state
    /// by following all transitions (both character and epsilon).
    /// </summary>
    private static IReadOnlyList<NfaState> CollectStates(NfaState start)
    {
        var visited = new HashSet<int>();
        var result = new List<NfaState>();
        var worklist = new Stack<NfaState>();

        worklist.Push(start);

        while(worklist.Count > 0)
        {
            var state = worklist.Pop();

            if(!visited.Add(state.Id))
            {
                continue;
            }

            result.Add(state);

            foreach(var transition in state.Transitions)
            {
                if(!visited.Contains(transition.Target.Id))
                {
                    worklist.Push(transition.Target);
                }
            }
        }

        return result;
    }
}