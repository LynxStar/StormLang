using System.Collections.Generic;

namespace Storm.SourceGenerator.Emit;

/// <summary>
/// Groups input characters (0–65535) with identical transition behavior
/// across all DFA states into equivalence classes. This compresses the
/// transition table width from 65536 columns to a much smaller number.
///
/// Optimization: instead of checking all 65536 characters individually,
/// we collect boundary points from the DFA's CharRange transitions to
/// form non-overlapping intervals. Characters within the same interval
/// always have the same transition behavior, so we only need to compute
/// one signature per interval.
/// </summary>
internal static class EquivalenceClassComputer
{
    /// <summary>
    /// Compute equivalence classes for the DFA's alphabet.
    /// Returns a mapping from each UTF-16 code unit to its class index,
    /// and the total number of distinct classes.
    /// </summary>
    public static (int[] charToClass, int classCount) Compute(Dfa dfa)
    {
        var charToClass = new int[65536];

        if(dfa.States.Count == 0)
        {
            // All characters map to class 0
            return (charToClass, 1);
        }

        // Task 6.3: Collect boundary points from all DFA transitions'
        // CharRange values to build non-overlapping intervals. This is
        // the same approach as SubsetConstructor.ComputeAlphabetPartition
        // but applied to the DFA's transitions.
        var intervals = ComputeDfaAlphabetPartition(dfa);

        if(intervals.Count == 0)
        {
            // No transitions at all — every character behaves the same
            return (charToClass, 1);
        }

        // Task 6.2: For each interval, compute its transition signature
        // across all DFA states, then group intervals with identical
        // signatures into the same equivalence class.
        var stateCount = dfa.States.Count;

        // Build a fast lookup: state ID -> index in States list
        var stateIdToIndex = new Dictionary<int, int>();
        for(var i = 0; i < dfa.States.Count; i++)
        {
            stateIdToIndex[dfa.States[i].Id] = i;
        }

        // Compute the signature for each interval. A signature is the
        // list of target state IDs (one per DFA state) for a representative
        // character from the interval. -1 means no transition.
        var signatureToClass = new Dictionary<string, int>();
        var nextClass = 0;

        // Track which class each interval gets assigned to
        var intervalClasses = new int[intervals.Count];

        for(var i = 0; i < intervals.Count; i++)
        {
            var signature = ComputeSignature(
                intervals[i].Lo, dfa, stateIdToIndex, stateCount);

            if(!signatureToClass.TryGetValue(signature, out var classIndex))
            {
                classIndex = nextClass++;
                signatureToClass[signature] = classIndex;
            }

            intervalClasses[i] = classIndex;
        }

        // Characters not covered by any interval (i.e., characters that
        // don't appear in any DFA transition) all have the same signature:
        // -1 for every state. Compute that signature and assign a class.
        var defaultSignature = ComputeDefaultSignature(stateCount);
        if(!signatureToClass.TryGetValue(defaultSignature, out var defaultClass))
        {
            defaultClass = nextClass++;
            signatureToClass[defaultSignature] = defaultClass;
        }

        // Fill the charToClass array. Start by setting everything to the
        // default class, then overwrite the intervals.
        for(var c = 0; c < 65536; c++)
        {
            charToClass[c] = defaultClass;
        }

        for(var i = 0; i < intervals.Count; i++)
        {
            int lo = intervals[i].Lo;
            int hi = intervals[i].Hi;
            var cls = intervalClasses[i];

            for(var c = lo; c <= hi; c++)
            {
                charToClass[c] = cls;
            }
        }

        return (charToClass, nextClass);
    }

    /// <summary>
    /// Collects all CharRange boundary points from the DFA's transitions
    /// and creates non-overlapping intervals. This mirrors the approach
    /// used in SubsetConstructor.ComputeAlphabetPartition but operates
    /// on DFA transitions instead of NFA transitions.
    /// </summary>
    private static List<CharRange> ComputeDfaAlphabetPartition(Dfa dfa)
    {
        var boundarySet = new SortedSet<int>();

        foreach(var state in dfa.States)
        {
            foreach(var transition in state.Transitions)
            {
                _ = boundarySet.Add(transition.Range.Lo);

                if(transition.Range.Hi < 0xFFFF)
                {
                    _ = boundarySet.Add(transition.Range.Hi + 1);
                }
            }
        }

        if(boundarySet.Count == 0)
        {
            return new List<CharRange>();
        }

        var boundaries = new List<int>(boundarySet);
        var result = new List<CharRange>();

        for(var i = 0; i < boundaries.Count; i++)
        {
            var lo = boundaries[i];
            var hi = i + 1 < boundaries.Count ? boundaries[i + 1] - 1 : 0xFFFF;
            if(lo <= hi && lo <= 0xFFFF)
            {
                result.Add(new CharRange((char)lo, (char)hi));
            }
        }

        return result;
    }

    /// <summary>
    /// Computes the transition signature for a representative character
    /// across all DFA states. The signature is a string encoding the
    /// target state ID for each DFA state (-1 if no transition).
    /// </summary>
    private static string ComputeSignature(
        char representative,
        Dfa dfa,
        Dictionary<int, int> stateIdToIndex,
        int stateCount)
    {
        // Build the signature as a comma-separated list of target state IDs
        // ordered by state index. Using a pre-allocated array avoids
        // repeated allocations.
        var targets = new int[stateCount];
        for(var i = 0; i < stateCount; i++)
        {
            targets[i] = -1;
        }

        for(var i = 0; i < dfa.States.Count; i++)
        {
            var state = dfa.States[i];
            if(!stateIdToIndex.TryGetValue(state.Id, out var stateIndex))
            {
                continue;
            }

            foreach(var transition in state.Transitions)
            {
                if(transition.Range.Contains(representative))
                {
                    targets[stateIndex] = transition.TargetStateId;
                    break;
                }
            }
        }

        return EncodeSignature(targets);
    }

    /// <summary>
    /// Returns the signature for characters that don't appear in any
    /// DFA transition — all targets are -1.
    /// </summary>
    private static string ComputeDefaultSignature(int stateCount)
    {
        var targets = new int[stateCount];
        for(var i = 0; i < stateCount; i++)
        {
            targets[i] = -1;
        }

        return EncodeSignature(targets);
    }

    /// <summary>
    /// Encodes an array of target state IDs into a canonical string
    /// for use as a dictionary key.
    /// </summary>
    private static string EncodeSignature(int[] targets)
    {
        var sb = new System.Text.StringBuilder();
        for(var i = 0; i < targets.Length; i++)
        {
            if(i > 0)
            {
                _ = sb.Append(',');
            }

            _ = sb.Append(targets[i]);
        }

        return sb.ToString();
    }
}