using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Analyze;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Property-based tests for dense declaration-order id assignment.
/// Feature: parser-memoization-expansion, Property 6: Dense declaration-order id assignment
/// </summary>
public class StormDenseIdAssignmentPropertyTests
{
    // ══════════════════════════════════════════════════════════════════
    //  Generators
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Generates a valid Storm rule name (PascalCase identifier).
    /// </summary>
    private static Gen<string> GenRuleName()
    {
        var upperChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();
        var lowerChars = "abcdefghijklmnopqrstuvwxyz".ToCharArray();

        return Gen.Elements(upperChars).SelectMany(first =>
            Gen.Choose(1, 8).SelectMany(len =>
                Gen.ArrayOf(Gen.Elements(lowerChars), len)
                    .Select(tail => first + new string(tail))));
    }

    /// <summary>
    /// Generates a list of unique rule names with @memo annotations such that
    /// the grammar will produce a non-empty MemoizationPlan. At least one rule
    /// is annotated with @memo and all rules are multi-alternative (speculative-safe).
    /// </summary>
    private static Gen<List<(string Name, bool IsMemo)>> GenMemoGrammarRules()
    {
        return Gen.Choose(2, 8).SelectMany(count =>
            Gen.ListOf(
                GenRuleName().SelectMany(name =>
                    Gen.Elements(true, false).Select(isMemo => (name, isMemo))),
                count
            ).Select(rules =>
            {
                // Deduplicate rule names — keep first occurrence
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var unique = new List<(string Name, bool IsMemo)>();
                foreach(var r in rules)
                {
                    if(seen.Add(r.name))
                    {
                        unique.Add((r.name, r.isMemo));
                    }
                }

                // Ensure at least one rule is @memo-annotated
                if(unique.Count > 0 && !unique.Any(r => r.IsMemo))
                {
                    unique[0] = (unique[0].Name, true);
                }

                return unique;
            }).Where(rules => rules.Count >= 1 && rules.Any(r => r.IsMemo)));
    }

    // ══════════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a Storm grammar source where each rule is multi-alternative
    /// (speculative-safe). Rules annotated with @memo will pass through the
    /// analysis pipeline and be included in the MemoizationPlan.
    /// </summary>
    private static string BuildGrammarSource(List<(string Name, bool IsMemo)> rules)
    {
        var sb = new System.Text.StringBuilder();
        _ = sb.AppendLine("token Tok = [a-z]+;");
        _ = sb.AppendLine("token Num = [0-9]+;");

        foreach(var (name, isMemo) in rules)
        {
            if(isMemo)
            {
                _ = sb.AppendLine("@memo");
            }

            // Multi-alternative rules are always speculative-safe
            _ = sb.AppendLine($"rule {name} = Tok | Num;");
        }

        return sb.ToString();
    }

    private static GrammarIR BuildGrammarIR(string source)
    {
        var grammar = ValidationTestHelpers.BuildGrammar(source);
        var lowering = GrammarIRLowering.Lower(grammar);

        Assert.True(
            lowering.Succeeded,
            "Grammar lowering failed: " +
                string.Join(", ", lowering.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(lowering.Grammar);

        return lowering.Grammar!;
    }

    // ══════════════════════════════════════════════════════════════════
    //  Property 6: Dense declaration-order id assignment
    //  For any non-empty MemoizationPlan, the assigned ids SHALL be a
    //  dense sequence 0..N-1 where N is the plan count, and the ordering
    //  SHALL match grammar declaration order.
    //  **Validates: Requirements 5.4**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 200)]
    public Property P6_DenseDeclarationOrderIdAssignment_IdsAreDenseZeroToNMinus1_InDeclarationOrder()
    {
        return Prop.ForAll(GenMemoGrammarRules().ToArbitrary(), rules =>
        {
            var source = BuildGrammarSource(rules);
            var grammar = BuildGrammarIR(source);
            var plan = MemoizationAnalysisPass.Run(grammar);

            // Only verify non-empty plans (property precondition)
            if(plan.Count == 0)
            {
                return;
            }

            // 1. Verify Count matches the number of memoized rules in RuleIds
            Assert.True(plan.Count == plan.RuleIds.Count,
                $"Plan.Count ({plan.Count}) != RuleIds.Count ({plan.RuleIds.Count}).\nSource:\n{source}");

            // 2. Verify ids are dense 0..N-1
            var assignedIds = plan.RuleIds.Values.OrderBy(id => id).ToList();
            var expectedIds = Enumerable.Range(0, plan.Count).ToList();
            Assert.True(expectedIds.SequenceEqual(assignedIds),
                $"Ids are not dense 0..{plan.Count - 1}. Got: [{string.Join(", ", assignedIds)}].\nSource:\n{source}");

            // 3. Verify ids follow grammar declaration order:
            //    Rules appearing earlier in grammar.Rules get lower ids.
            var memoizedInDeclarationOrder = grammar.Rules
                .Select(r => r.Name)
                .Where(plan.IsMemoized)
                .ToList();

            Assert.True(memoizedInDeclarationOrder.Count == plan.Count,
                $"Memoized rule count mismatch: declaration-order filter gave " +
                $"{memoizedInDeclarationOrder.Count} but plan has {plan.Count}.\nSource:\n{source}");

            // Each rule at position i in declaration order must have id == i
            for(var i = 0; i < memoizedInDeclarationOrder.Count; i++)
            {
                var name = memoizedInDeclarationOrder[i];
                var actualId = plan.RuleIds[name];
                Assert.True(actualId == i,
                    $"Rule '{name}' at declaration position {i} has id {actualId} (expected {i}).\nSource:\n{source}");
            }

            // 4. Verify MemoizedRuleNames matches declaration order
            Assert.True(memoizedInDeclarationOrder.SequenceEqual(plan.MemoizedRuleNames),
                $"MemoizedRuleNames does not match declaration order.\n" +
                $"Expected: [{string.Join(", ", memoizedInDeclarationOrder)}]\n" +
                $"Actual: [{string.Join(", ", plan.MemoizedRuleNames)}]\nSource:\n{source}");
        });
    }
}