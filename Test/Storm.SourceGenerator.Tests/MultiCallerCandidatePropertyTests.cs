using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Analyze;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Property-based tests for multi-caller candidate detection.
/// Feature: parser-memoization-expansion, Property 4: Multi-caller candidate detection
///
/// For any grammar IR where a rule R is referenced from two or more distinct alternatives
/// of any parent rule's alternation, R appears in the candidate set (the final
/// MemoizationPlan), assuming R passes IsSpeculativeSafe — which all simple rules do.
///
/// **Validates: Requirements 4.1, 4.2**
/// </summary>
public class MultiCallerCandidatePropertyTests
{

    /// <summary>
    /// Describes a grammar with multi-alternative parent rules that reference
    /// shared child rules. The generated structure guarantees at least one rule
    /// is referenced from 2+ distinct alternatives of a parent.
    /// </summary>
    private sealed class MultiCallerGrammar
    {
        /// <summary>Number of leaf rules (potential multi-caller candidates).</summary>
        public int LeafCount { get; init; }

        /// <summary>Number of parent rules with 2+ alternatives.</summary>
        public int ParentCount { get; init; }

        /// <summary>
        /// For each parent rule, the list of alternatives where each alternative
        /// is a set of leaf rule indices it references.
        /// </summary>
        public List<List<List<int>>> ParentAlternatives { get; init; } = new();

        public override string ToString()
        {
            return $"MultiCallerGrammar(leaves={LeafCount}, parents={ParentCount}, " +
                   $"alts=[{string.Join(", ", ParentAlternatives.Select(p => $"[{string.Join("|", p.Select(a => string.Join(",", a)))}]"))}])";
        }
    }

    /// <summary>
    /// Generates a <see cref="MultiCallerGrammar"/> that is guaranteed to have at least
    /// one rule appearing in 2+ distinct alternatives of some parent rule.
    /// </summary>
    private static Gen<MultiCallerGrammar> GenMultiCallerGrammar()
    {
        return Gen.Choose(2, 6).SelectMany(leafCount =>
            Gen.Choose(1, 3).SelectMany(parentCount =>
            {
                // Generate alternatives for each parent rule.
                // Each parent has 2-4 alternatives, each alternative references 1-3 leaf rules.
                var parentAltsGen = Gen.ListOf(
                    Gen.Choose(2, 4).SelectMany(altCount =>
                        Gen.ListOf(
                            Gen.Choose(1, Math.Min(3, leafCount)).SelectMany(refCount =>
                                Gen.ListOf(
                                    Gen.Choose(0, leafCount - 1),
                                    refCount
                                ).Select(refs => refs.Distinct().ToList())),
                            altCount
                        ).Select(alts => alts.ToList())),
                    parentCount
                );

                return parentAltsGen.Select(parentAlts =>
                {
                    var alts = parentAlts.ToList();

                    // Ensure at least one leaf rule appears in 2+ alternatives of some parent.
                    // Pick leaf 0 and ensure it appears in at least 2 alternatives of parent 0.
                    if(alts.Count > 0 && alts[0].Count >= 2)
                    {
                        // Force leaf 0 into the first two alternatives of parent 0
                        if(!alts[0][0].Contains(0))
                        {
                            alts[0][0].Add(0);
                        }

                        if(!alts[0][1].Contains(0))
                        {
                            alts[0][1].Add(0);
                        }
                    }

                    return new MultiCallerGrammar
                    {
                        LeafCount = leafCount,
                        ParentCount = parentCount,
                        ParentAlternatives = alts
                    };
                });
            }));
    }

    // ══════════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a <see cref="GrammarIR"/> directly from a <see cref="MultiCallerGrammar"/> descriptor.
    /// Leaf rules are single-alternative rules referencing a shared token (speculative-safe).
    /// Parent rules have multiple alternatives, each referencing a subset of leaf rules.
    /// </summary>
    private static GrammarIR BuildGrammarIR(MultiCallerGrammar desc)
    {
        // Create a shared token for leaf rule bodies
        var sharedToken = new TokenIR("TOK", new CharClassIR("[a-z]"), isHidden: false, declarationIndex: 0);
        var tokens = new List<TokenIR> { sharedToken };

        // Create leaf rules (single-alternative, referencing only a token — always speculative-safe)
        var leafRules = new List<RuleIR>();
        for(var i = 0; i < desc.LeafCount; i++)
        {
            var name = $"Rule{(char)('A' + i)}";
            var alt = new RuleAlternativeIR(null, Array.Empty<FieldLabelIR>(), new TokenRefIR(sharedToken));
            var rule = new RuleIR(name, new[] { alt });
            leafRules.Add(rule);
        }

        // Create parent rules with multiple alternatives referencing leaf rules
        var parentRules = new List<RuleIR>();
        for(var p = 0; p < desc.ParentCount; p++)
        {
            var parentName = $"Parent{(char)('A' + p)}";
            var alternatives = new List<RuleAlternativeIR>();

            foreach(var altRefs in desc.ParentAlternatives[p])
            {
                ExpressionIR expr;
                if(altRefs.Count == 0)
                {
                    // Empty alternative just references the token
                    expr = new TokenRefIR(sharedToken);
                }
                else if(altRefs.Count == 1)
                {
                    expr = new RuleRefIR(leafRules[altRefs[0]]);
                }
                else
                {
                    // Sequence of rule references
                    var elements = altRefs
                        .Select(idx => (ExpressionIR)new RuleRefIR(leafRules[idx]))
                        .ToList();
                    expr = new SequenceIR(elements);
                }

                alternatives.Add(new RuleAlternativeIR(null, Array.Empty<FieldLabelIR>(), expr));
            }

            parentRules.Add(new RuleIR(parentName, alternatives));
        }

        // Combine: leaf rules first, then parent rules (declaration order)
        var allRules = new List<RuleIR>();
        allRules.AddRange(leafRules);
        allRules.AddRange(parentRules);

        return new GrammarIR(tokens, allRules);
    }

    /// <summary>
    /// Computes the set of leaf rule names that appear in 2+ distinct alternatives
    /// of any parent rule in the grammar descriptor. These are the expected
    /// multi-caller candidates.
    /// </summary>
    private static HashSet<string> ComputeExpectedMultiCallerCandidates(MultiCallerGrammar desc)
    {
        var candidates = new HashSet<string>();

        foreach(var parentAlts in desc.ParentAlternatives)
        {
            // For each leaf rule, count how many distinct alternatives reference it
            var leafAltCount = new Dictionary<int, int>();
            foreach(var altRefs in parentAlts)
            {
                // Each alternative's distinct leaf indices
                var distinctInAlt = new HashSet<int>(altRefs);
                foreach(var leafIdx in distinctInAlt)
                {
                    _ = leafAltCount.TryGetValue(leafIdx, out var count);
                    leafAltCount[leafIdx] = count + 1;
                }
            }

            foreach(var (leafIdx, count) in leafAltCount)
            {
                if(count >= 2)
                {
                    _ = candidates.Add($"Rule{(char)('A' + leafIdx)}");
                }
            }
        }

        return candidates;
    }

    // ══════════════════════════════════════════════════════════════════
    //  Property 4: Multi-caller candidate detection
    //  For any grammar IR where a rule R is referenced from 2+ distinct
    //  alternatives of any parent rule's alternation, R appears in the
    //  candidate set (the MemoizationPlan).
    //  **Validates: Requirements 4.1, 4.2**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 200)]
    public Property P4_MultiCallerDetection_RulesIn2PlusAlternatives_AppearInPlan()
    {
        return Prop.ForAll(GenMultiCallerGrammar().ToArbitrary(), desc =>
        {
            var grammar = BuildGrammarIR(desc);
            var plan = MemoizationAnalysisPass.Run(grammar);

            var expectedCandidates = ComputeExpectedMultiCallerCandidates(desc);

            // Every expected multi-caller candidate must appear in the plan.
            // (All leaf rules are speculative-safe, so no filtering occurs.)
            foreach(var candidateName in expectedCandidates)
            {
                Assert.True(plan.IsMemoized(candidateName),
                    $"Rule '{candidateName}' is referenced from 2+ distinct alternatives " +
                    $"but was NOT included in the MemoizationPlan.\n" +
                    $"Grammar descriptor: {desc}");
            }
        });
    }
}