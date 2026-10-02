using System.Collections.Generic;

namespace Storm.SourceGenerator.Analyze;

/// <summary>
/// The set of rules selected for packrat memoization, with a stable integer id
/// per memoized rule. Ids are assigned in grammar declaration order so generated
/// output is deterministic across builds.
/// </summary>
/// <remarks>
/// This is the immutable contract between the memoization analysis pass and the
/// parser emitter. It carries no behavior beyond lookups. Ids are guaranteed to be
/// dense and 0-based (<c>0..Count-1</c>), assigned one-per-rule in the order of
/// <see cref="MemoizedRuleNames"/>.
/// </remarks>
public sealed class MemoizationPlan
{

    /// <summary>
    /// The memoized rule names, in grammar declaration order. The rule at index
    /// <c>i</c> is assigned id <c>i</c>.
    /// </summary>
    public IReadOnlyList<string> MemoizedRuleNames { get; }

    /// <summary>
    /// Maps a memoized rule name to its stable integer id (0-based, dense).
    /// </summary>
    public IReadOnlyDictionary<string, int> RuleIds { get; }

    /// <summary>
    /// The number of memoized rules in this plan.
    /// </summary>
    public int Count => MemoizedRuleNames.Count;

    /// <summary>
    /// Creates a plan from an ordered list of memoized rule names. Each rule is
    /// assigned a dense, 0-based id equal to its position in <paramref name="memoizedRuleNames"/>,
    /// guaranteeing ids <c>0..Count-1</c> with no gaps and a one-to-one mapping.
    /// </summary>
    /// <param name="memoizedRuleNames">
    /// The memoized rule names, in grammar declaration order.
    /// </param>
    public MemoizationPlan(IReadOnlyList<string> memoizedRuleNames)
    {
        var names = new List<string>(memoizedRuleNames);
        var ids = new Dictionary<string, int>(names.Count);
        for(var i = 0; i < names.Count; i++)
        {
            ids[names[i]] = i;
        }

        MemoizedRuleNames = names;
        RuleIds = ids;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the named rule is selected for memoization.
    /// </summary>
    public bool IsMemoized(string ruleName)
    {
        return RuleIds.ContainsKey(ruleName);
    }

    /// <summary>
    /// An empty plan: nothing memoized. Used as a safe fallback so an emitter
    /// constructed without a plan produces output identical to the pre-feature parser.
    /// </summary>
    public static MemoizationPlan Empty { get; } = new MemoizationPlan(new List<string>());
}