using System.Collections.Generic;

namespace Storm.SourceGenerator.Emit;

/// <summary>
/// Converts an NFA to an equivalent DFA using the subset (powerset)
/// construction algorithm. Each DFA state corresponds to a set of
/// NFA states. Accepting DFA states get the token with the lowest
/// declaration index among the NFA accepting states in the set.
/// </summary>
internal static class SubsetConstructor
{
    /// <summary>
    /// Convert an NFA to an equivalent DFA.
    /// </summary>
    public static Dfa Convert(Nfa nfa)
    {
        // Build a lookup from NFA state ID to NfaState for fast access
        var nfaStatesById = new Dictionary<int, NfaState>();
        foreach(var state in nfa.States)
        {
            nfaStatesById[state.Id] = state;
        }

        // Compute the alphabet partition (non-overlapping intervals)
        var alphabet = ComputeAlphabetPartition(nfa);

        // Subset construction worklist algorithm
        var dfaStates = new List<DfaState>();
        // Map from sorted NFA state set (as string key) to DFA state ID
        var stateSetToDfaId = new Dictionary<string, int>();

        // Start with epsilon closure of the NFA start state
        var startSet = EpsilonClosure(new HashSet<int> { nfa.StartState.Id }, nfaStatesById);
        var startKey = MakeKey(startSet);

        var startDfaState = new DfaState(0)
        {
            AcceptingTokenIndex = ResolveAcceptingToken(startSet, nfaStatesById)
        };
        dfaStates.Add(startDfaState);
        stateSetToDfaId[startKey] = 0;

        var worklist = new Queue<int>();
        // Also keep the NFA state sets indexed by DFA state ID
        var dfaIdToNfaSet = new List<HashSet<int>>
        {
            startSet
        };
        worklist.Enqueue(0);

        while(worklist.Count > 0)
        {
            var dfaId = worklist.Dequeue();
            var nfaSet = dfaIdToNfaSet[dfaId];
            var dfaState = dfaStates[dfaId];

            foreach(var range in alphabet)
            {
                var targetNfaSet = Move(nfaSet, range, nfaStatesById);
                if(targetNfaSet.Count == 0)
                {
                    continue;
                }

                var closedSet = EpsilonClosure(targetNfaSet, nfaStatesById);
                var key = MakeKey(closedSet);

                if(!stateSetToDfaId.TryGetValue(key, out var targetDfaId))
                {
                    targetDfaId = dfaStates.Count;
                    var newDfaState = new DfaState(targetDfaId)
                    {
                        AcceptingTokenIndex = ResolveAcceptingToken(closedSet, nfaStatesById)
                    };
                    dfaStates.Add(newDfaState);
                    stateSetToDfaId[key] = targetDfaId;
                    dfaIdToNfaSet.Add(closedSet);
                    worklist.Enqueue(targetDfaId);
                }

                dfaState.Transitions.Add(new DfaTransition(range, targetDfaId));
            }
        }

        return new Dfa(0, dfaStates);
    }

    // ── Task 4.4: Epsilon Closure ────────────────────────────────────

    /// <summary>
    /// Computes the epsilon closure of a set of NFA states — all states
    /// reachable from the input set by following only epsilon transitions.
    /// Uses BFS with a visited set.
    /// </summary>
    internal static HashSet<int> EpsilonClosure(
        HashSet<int> stateIds,
        Dictionary<int, NfaState> statesById)
    {
        var closure = new HashSet<int>(stateIds);
        var worklist = new Queue<int>(stateIds);

        while(worklist.Count > 0)
        {
            var id = worklist.Dequeue();
            if(!statesById.TryGetValue(id, out var state))
            {
                continue;
            }

            foreach(var transition in state.Transitions)
            {
                if(transition.IsEpsilon && closure.Add(transition.Target.Id))
                {
                    worklist.Enqueue(transition.Target.Id);
                }
            }
        }

        return closure;
    }

    // ── Task 4.5: Alphabet Partitioning ──────────────────────────────

    /// <summary>
    /// Collects all CharRange boundaries from all NFA transitions and
    /// splits them into non-overlapping intervals that cover the same
    /// character space. This ensures that each interval either fully
    /// triggers or fully does not trigger any given NFA transition.
    /// </summary>
    internal static List<CharRange> ComputeAlphabetPartition(Nfa nfa)
    {
        // Collect all boundary points from all character transitions.
        // For each CharRange [lo, hi], we add lo and hi+1 as boundary points.
        // This creates a set of "cut points" that partition the character space.
        var boundarySet = new SortedSet<int>();

        foreach(var state in nfa.States)
        {
            foreach(var transition in state.Transitions)
            {
                if(transition.Ranges == null)
                {
                    continue;
                }

                foreach(var range in transition.Ranges)
                {
                    _ = boundarySet.Add(range.Lo);
                    // Add hi+1 as a boundary (if it doesn't overflow)
                    if(range.Hi < 0xFFFF)
                    {
                        _ = boundarySet.Add(range.Hi + 1);
                    }
                }
            }
        }

        if(boundarySet.Count == 0)
        {
            return new List<CharRange>();
        }

        // Convert boundary points to sorted list
        var boundaries = new List<int>(boundarySet);

        // Create non-overlapping intervals from consecutive boundary points
        var result = new List<CharRange>();

        for(var i = 0; i < boundaries.Count; i++)
        {
            var lo = boundaries[i];
            int hi;

            if(i + 1 < boundaries.Count)
            {
                hi = boundaries[i + 1] - 1;
            }
            else
            {
                // Last boundary extends to the end of the UTF-16 range
                hi = 0xFFFF;
            }

            if(lo <= hi && lo <= 0xFFFF)
            {
                result.Add(new CharRange((char)lo, (char)hi));
            }
        }

        return result;
    }

    // ── Task 4.6: Move (character transition) ────────────────────────

    /// <summary>
    /// Computes the set of NFA states reachable from the given set by
    /// following character transitions that match the given range.
    /// A transition matches if any of its CharRanges overlaps with the
    /// given range (since the alphabet is partitioned into non-overlapping
    /// intervals, we check if the interval intersects any transition range).
    /// </summary>
    private static HashSet<int> Move(
        HashSet<int> stateIds,
        CharRange range,
        Dictionary<int, NfaState> statesById)
    {
        var result = new HashSet<int>();
        // Use a representative character from the range to test membership
        var representative = range.Lo;

        foreach(var id in stateIds)
        {
            if(!statesById.TryGetValue(id, out var state))
            {
                continue;
            }

            foreach(var transition in state.Transitions)
            {
                if(transition.Ranges == null)
                {
                    continue;
                }

                foreach(var tRange in transition.Ranges)
                {
                    if(tRange.Contains(representative))
                    {
                        _ = result.Add(transition.Target.Id);
                        break;
                    }
                }
            }
        }

        return result;
    }

    // ── Task 4.7: Accepting State Resolution ─────────────────────────

    /// <summary>
    /// Given a set of NFA state IDs, finds the lowest accepting token
    /// index among all accepting states in the set. Returns -1 if no
    /// state in the set is accepting.
    /// </summary>
    private static int ResolveAcceptingToken(
        HashSet<int> stateIds,
        Dictionary<int, NfaState> statesById)
    {
        var bestIndex = -1;

        foreach(var id in stateIds)
        {
            if(!statesById.TryGetValue(id, out var state))
            {
                continue;
            }

            if(state.AcceptingTokenIndex >= 0)
            {
                if(bestIndex < 0 || state.AcceptingTokenIndex < bestIndex)
                {
                    bestIndex = state.AcceptingTokenIndex;
                }
            }
        }

        return bestIndex;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Creates a canonical string key for a set of NFA state IDs.
    /// The IDs are sorted so that identical sets produce identical keys.
    /// </summary>
    private static string MakeKey(HashSet<int> stateIds)
    {
        var sorted = new List<int>(stateIds);
        sorted.Sort();

        // Use a compact representation
        var sb = new System.Text.StringBuilder();
        for(var i = 0; i < sorted.Count; i++)
        {
            if(i > 0)
            {
                _ = sb.Append(',');
            }

            _ = sb.Append(sorted[i]);
        }

        return sb.ToString();
    }
}