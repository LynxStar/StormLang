using System.Collections.Generic;

namespace Storm.SourceGenerator.Emit;

/// <summary>
/// Minimizes a DFA using Hopcroft's algorithm by merging
/// indistinguishable states. Two states are distinguishable if they
/// have different accepting token indices or if any input leads to
/// distinguishable states.
/// </summary>
internal static class DfaMinimizer
{
    // ── Task 5.5: Top-level Minimize ─────────────────────────────────

    /// <summary>
    /// Minimize the DFA by merging indistinguishable states.
    /// Returns a new <see cref="Dfa"/> with the fewest possible states
    /// that recognizes the same language and preserves accepting annotations.
    /// </summary>
    public static Dfa Minimize(Dfa dfa)
    {
        if(dfa.States.Count == 0)
        {
            return new Dfa(dfa.StartStateId, new List<DfaState>());
        }

        // Collect all distinct CharRange values used across every transition
        // in the DFA. These are the "input symbols" for partition refinement.
        var alphabet = CollectAlphabet(dfa);

        // Task 5.2: Build the initial partition
        var partitions = BuildInitialPartition(dfa);

        // Task 5.3: Refine partitions until stable
        RefinePartitions(dfa, partitions, alphabet);

        // Task 5.4: Construct the minimized DFA from final partitions
        return BuildMinimizedDfa(dfa, partitions);
    }

    // ── Task 5.2: Initial Partition ──────────────────────────────────

    /// <summary>
    /// Creates the initial partition for Hopcroft's algorithm.
    /// States are grouped by their accepting token index:
    /// all non-accepting states (AcceptingTokenIndex == -1) form one group,
    /// and each distinct accepting token index forms its own group.
    /// </summary>
    private static List<List<int>> BuildInitialPartition(Dfa dfa)
    {
        // Map from accepting token index to partition group.
        // Key -1 = non-accepting states.
        var groups = new Dictionary<int, List<int>>();

        for(var i = 0; i < dfa.States.Count; i++)
        {
            var state = dfa.States[i];
            var key = state.AcceptingTokenIndex;

            if(!groups.TryGetValue(key, out var group))
            {
                group = new List<int>();
                groups[key] = group;
            }

            group.Add(state.Id);
        }

        var partitions = new List<List<int>>();
        foreach(var kvp in groups)
        {
            partitions.Add(kvp.Value);
        }

        return partitions;
    }

    // ── Task 5.3: Partition Refinement ───────────────────────────────

    /// <summary>
    /// Refines partitions until no more splits occur. For each partition
    /// and each input symbol (CharRange), checks whether all states in
    /// the partition transition to the same target partition. If not,
    /// splits the partition into sub-groups where states that go to the
    /// same target partition are grouped together.
    /// </summary>
    private static void RefinePartitions(
        Dfa dfa,
        List<List<int>> partitions,
        List<CharRange> alphabet)
    {
        // Build a fast lookup: state ID -> index into the States list
        // (state IDs may not be contiguous or zero-based, so use a map)
        var stateById = new Dictionary<int, DfaState>();
        foreach(var state in dfa.States)
        {
            stateById[state.Id] = state;
        }

        var changed = true;
        while(changed)
        {
            changed = false;

            var newPartitions = new List<List<int>>();

            foreach(var partition in partitions)
            {
                if(partition.Count <= 1)
                {
                    newPartitions.Add(partition);
                    continue;
                }

                // Try to split this partition on each symbol
                var currentSplit = new List<List<int>> { partition };

                foreach(var range in alphabet)
                {
                    var nextSplit = new List<List<int>>();

                    foreach(var group in currentSplit)
                    {
                        if(group.Count <= 1)
                        {
                            nextSplit.Add(group);
                            continue;
                        }

                        // Split this group based on which partition the
                        // transition on 'range' leads to.
                        var splitResult = SplitOnSymbol(
                            group, range, stateById, partitions);
                        nextSplit.AddRange(splitResult);
                    }

                    currentSplit = nextSplit;
                }

                if(currentSplit.Count > 1)
                {
                    changed = true;
                }

                newPartitions.AddRange(currentSplit);
            }

            partitions.Clear();
            partitions.AddRange(newPartitions);
        }
    }

    /// <summary>
    /// Splits a group of state IDs based on which partition their
    /// transition on the given CharRange leads to. States with no
    /// transition for this range are treated as going to a special
    /// "dead" partition (represented by -1).
    /// </summary>
    private static List<List<int>> SplitOnSymbol(
        List<int> group,
        CharRange range,
        Dictionary<int, DfaState> stateById,
        List<List<int>> partitions)
    {
        // Build a lookup from state ID to partition index for fast access
        var stateToPartition = new Dictionary<int, int>();
        for(var p = 0; p < partitions.Count; p++)
        {
            foreach(var sid in partitions[p])
            {
                stateToPartition[sid] = p;
            }
        }

        // Group states by their target partition index.
        // -1 means "dead state" (no transition for this symbol).
        var subGroups = new Dictionary<int, List<int>>();

        foreach(var stateId in group)
        {
            var targetPartition = GetTargetPartition(
                stateId, range, stateById, stateToPartition);

            if(!subGroups.TryGetValue(targetPartition, out var subGroup))
            {
                subGroup = new List<int>();
                subGroups[targetPartition] = subGroup;
            }

            subGroup.Add(stateId);
        }

        var result = new List<List<int>>();
        foreach(var kvp in subGroups)
        {
            result.Add(kvp.Value);
        }

        return result;
    }

    /// <summary>
    /// Returns the partition index that the given state transitions to
    /// on the given CharRange. Returns -1 (dead partition) if the state
    /// has no transition for this range.
    /// </summary>
    private static int GetTargetPartition(
        int stateId,
        CharRange range,
        Dictionary<int, DfaState> stateById,
        Dictionary<int, int> stateToPartition)
    {
        if(!stateById.TryGetValue(stateId, out var state))
        {
            return -1;
        }

        // Find the transition matching this range.
        // Since the alphabet comes from the DFA's own transitions,
        // we check if the range matches any transition's range exactly
        // or if the range's representative character falls within a
        // transition's range.
        var representative = range.Lo;
        foreach(var transition in state.Transitions)
        {
            if(transition.Range.Contains(representative))
            {
                if(stateToPartition.TryGetValue(transition.TargetStateId, out var targetPartition))
                {
                    return targetPartition;
                }
                // Target state not in any partition — treat as dead
                return -1;
            }
        }

        // No transition for this symbol — dead state
        return -1;
    }

    // ── Task 5.4: Minimized DFA Construction ─────────────────────────

    /// <summary>
    /// Creates a new minimized DFA from the final partitions. Each
    /// partition becomes one DFA state. The representative state of
    /// each partition provides the accepting token index. Transitions
    /// are mapped from partition representatives to target partitions.
    /// </summary>
    private static Dfa BuildMinimizedDfa(
        Dfa dfa,
        List<List<int>> partitions)
    {
        // Build a lookup: old state ID -> partition index (new state ID)
        var stateToPartition = new Dictionary<int, int>();
        for(var p = 0; p < partitions.Count; p++)
        {
            foreach(var sid in partitions[p])
            {
                stateToPartition[sid] = p;
            }
        }

        // Build a lookup from old state ID to DfaState
        var stateById = new Dictionary<int, DfaState>();
        foreach(var state in dfa.States)
        {
            stateById[state.Id] = state;
        }

        // Create new DFA states, one per partition
        var newStates = new List<DfaState>();
        for(var p = 0; p < partitions.Count; p++)
        {
            var newState = new DfaState(p);

            // Use the first state in the partition as the representative.
            // All states in the partition have the same accepting token
            // index (guaranteed by the initial partition).
            var representativeId = partitions[p][0];
            if(stateById.TryGetValue(representativeId, out var representative))
            {
                newState.AcceptingTokenIndex = representative.AcceptingTokenIndex;

                // Map transitions: for each transition of the representative,
                // find the target state's partition and create a transition
                // to that partition's new state ID.
                var seenTargets = new HashSet<string>();
                foreach(var transition in representative.Transitions)
                {
                    if(stateToPartition.TryGetValue(transition.TargetStateId, out var targetPartition))
                    {
                        // Avoid duplicate transitions (multiple old transitions
                        // could map to the same new target with the same range)
                        var key = transition.Range.Lo + "-" + transition.Range.Hi + "->" + targetPartition;
                        if(seenTargets.Add(key))
                        {
                            newState.Transitions.Add(
                                new DfaTransition(transition.Range, targetPartition));
                        }
                    }
                }
            }

            newStates.Add(newState);
        }

        // The start state's partition becomes the new start state
        if(!stateToPartition.TryGetValue(dfa.StartStateId, out var newStartStateId))
        {
            newStartStateId = 0;
        }

        return new Dfa(newStartStateId, newStates);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Collects all distinct CharRange values used across all transitions
    /// in the DFA. These serve as the input symbols for partition refinement.
    /// </summary>
    private static List<CharRange> CollectAlphabet(Dfa dfa)
    {
        var seen = new HashSet<CharRange>();
        var alphabet = new List<CharRange>();

        foreach(var state in dfa.States)
        {
            foreach(var transition in state.Transitions)
            {
                if(seen.Add(transition.Range))
                {
                    alphabet.Add(transition.Range);
                }
            }
        }

        return alphabet;
    }
}