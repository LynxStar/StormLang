using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Analyze;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Property-based tests for candidate union with speculative-safety filter.
/// Feature: parser-memoization-expansion, Property 5: Candidate union with speculative-safety filter
/// </summary>
public class StormCandidateUnionPropertyTests
{

    /// <summary>
    /// Configuration for a generated grammar that exercises all three candidate sources.
    /// </summary>
    private sealed class GrammarScenario
    {
        /// <summary>Rules that participate in a recursive cycle with backtracking (cycle candidates).</summary>
        public required List<string> CycleRules { get; init; }

        /// <summary>Rules that appear in 2+ alternatives of a parent (multi-caller candidates).</summary>
        public required List<string> MultiCallerRules { get; init; }

        /// <summary>Rules annotated with @memo (memo candidates).</summary>
        public required List<string> MemoRules { get; init; }

        /// <summary>Rules that don't qualify through any source (should NOT be memoized).</summary>
        public required List<string> PlainRules { get; init; }

        /// <summary>The generated grammar source.</summary>
        public required string Source { get; init; }
    }

    /// <summary>
    /// Generates grammar scenarios that exercise all three candidate sources independently
    /// and in combination. Each scenario contains:
    /// - A recursive cycle with backtracking (cycle candidates)
    /// - Rules referenced in 2+ alternatives of a parent (multi-caller candidates)
    /// - @memo-annotated rules (memo candidates)
    /// - Plain rules that don't qualify through any source
    /// </summary>
    private static Gen<GrammarScenario> GenGrammarScenario()
    {
        // We generate scenarios with a fixed structure that exercises all three sources.
        // Variable parts: names of rules, whether memo annotation is added to some.
        return Gen.Choose(0, 3).SelectMany(memoVariant =>
            Gen.Choose(0, 1).Select(extraMultiCaller =>
            {
                var sb = new System.Text.StringBuilder();
                _ = sb.AppendLine("grammar Test;");
                _ = sb.AppendLine("token TOK = [a-z]+;");
                _ = sb.AppendLine("token NUM = [0-9]+;");
                _ = sb.AppendLine("token COMMA = \",\";");
                _ = sb.AppendLine("token PIPE = \"|\";");

                var cycleRules = new List<string>();
                var multiCallerRules = new List<string>();
                var memoRules = new List<string>();
                var plainRules = new List<string>();

                // --- Cycle candidate rules ---
                // Create a 2-rule recursive cycle with backtracking:
                //   CycleA -> CycleB (via alternative)
                //   CycleB -> CycleA (via repetition tail)
                // Both have backtracking (CycleA: multi-alt, CycleB: repetition)
                cycleRules.Add("CycleA");
                cycleRules.Add("CycleB");
                _ = sb.AppendLine("rule CycleA = TOK CycleB | NUM;");
                _ = sb.AppendLine("rule CycleB = CycleA (COMMA CycleA)*;");

                // --- Multi-caller candidate rules ---
                // Create a rule referenced in 2+ alternatives of a parent:
                //   Parent -> MultiTarget PIPE TOK | MultiTarget NUM
                // MultiTarget is a multi-caller candidate.
                multiCallerRules.Add("MultiTarget");
                _ = sb.AppendLine("rule MultiTarget = TOK | NUM;");
                _ = sb.AppendLine("rule Parent = MultiTarget PIPE TOK | MultiTarget NUM;");

                // Optionally add another multi-caller target
                if(extraMultiCaller == 1)
                {
                    multiCallerRules.Add("ExtraMulti");
                    _ = sb.AppendLine("rule ExtraMulti = TOK;");
                    _ = sb.AppendLine("rule Parent2 = ExtraMulti COMMA TOK | ExtraMulti PIPE NUM;");
                }

                // --- @memo candidate rules ---
                // Add @memo annotated rules that are not otherwise candidates
                if(memoVariant >= 1)
                {
                    memoRules.Add("MemoOnly");
                    _ = sb.AppendLine("@memo");
                    _ = sb.AppendLine("rule MemoOnly = TOK COMMA NUM;");
                }

                if(memoVariant >= 2)
                {
                    memoRules.Add("MemoTwo");
                    _ = sb.AppendLine("@memo");
                    _ = sb.AppendLine("rule MemoTwo = NUM | TOK;");
                }

                if(memoVariant >= 3)
                {
                    // A rule that is BOTH a multi-caller candidate AND @memo annotated
                    // It should appear once in the plan (union deduplicates)
                    memoRules.Add("MemoPlusMulti");
                    multiCallerRules.Add("MemoPlusMulti");
                    _ = sb.AppendLine("@memo");
                    _ = sb.AppendLine("rule MemoPlusMulti = TOK;");
                    _ = sb.AppendLine("rule Parent3 = MemoPlusMulti COMMA TOK | MemoPlusMulti PIPE NUM;");
                }

                // --- Plain rules (not candidates through any source) ---
                // Single-alternative, no cycle, no multi-caller, no @memo
                plainRules.Add("PlainRule");
                _ = sb.AppendLine("rule PlainRule = TOK;");
                plainRules.Add("AnotherPlain");
                _ = sb.AppendLine("rule AnotherPlain = NUM COMMA TOK;");

                return new GrammarScenario
                {
                    CycleRules = cycleRules,
                    MultiCallerRules = multiCallerRules,
                    MemoRules = memoRules,
                    PlainRules = plainRules,
                    Source = sb.ToString(),
                };
            }));
    }

    // ══════════════════════════════════════════════════════════════════
    //  Property 5: Candidate union with speculative-safety filter
    //  For any grammar IR, the final MemoizationPlan contains exactly
    //  those rules that (a) appear in the union of cycle-based candidates,
    //  multi-caller candidates, and @memo-annotated rules, AND (b) pass
    //  the IsSpeculativeSafe check.
    //
    //  Since IsSpeculativeSafe passes all grammar-expressible rules in the
    //  current emitter (multi-alternative rules are always safe, and
    //  single-alternative rules have no committing token match), the
    //  property reduces to: the plan contains exactly those rules that
    //  are in the union of all three candidate sources.
    //
    //  **Validates: Requirements 5.1, 5.2**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 100)]
    public Property P5_CandidateUnion_PlanContainsExactlyUnionOfAllSources()
    {
        return Prop.ForAll(GenGrammarScenario().ToArbitrary(), scenario =>
        {
            // Strip the "grammar Test;\n" prefix that BuildGrammar will add
            // Our source already has it, so we pass it directly through the full pipeline
            var grammarIR = BuildGrammarIRFromFullSource(scenario.Source);
            var diagnostics = new List<StormDiagnostic>();
            var plan = MemoizationAnalysisPass.Run(grammarIR, diagnostics);

            // Compute expected: union of all three candidate sources
            var expectedCandidates = new HashSet<string>();
            foreach(var name in scenario.CycleRules)
            {
                _ = expectedCandidates.Add(name);
            }

            foreach(var name in scenario.MultiCallerRules)
            {
                _ = expectedCandidates.Add(name);
            }

            foreach(var name in scenario.MemoRules)
            {
                _ = expectedCandidates.Add(name);
            }

            // All grammar-expressible rules pass IsSpeculativeSafe in the current emitter,
            // so the expected plan is exactly the union of all candidate sources.

            // Verify: every expected candidate IS in the plan
            foreach(var name in expectedCandidates)
            {
                Assert.True(plan.IsMemoized(name),
                    $"Rule '{name}' should be in the MemoizationPlan (it's a candidate from one or more sources) " +
                    $"but was not found.\nSource:\n{scenario.Source}");
            }

            // Verify: no PLAIN rule (non-candidate) is in the plan
            foreach(var name in scenario.PlainRules)
            {
                Assert.False(plan.IsMemoized(name),
                    $"Rule '{name}' should NOT be in the MemoizationPlan (it's not a candidate from any source) " +
                    $"but was found.\nSource:\n{scenario.Source}");
            }

            // Verify: the plan count matches the expected candidate count
            // (plan contains exactly the union, no more, no less)
            Assert.True(plan.Count == expectedCandidates.Count,
                $"Expected {expectedCandidates.Count} memoized rules but got {plan.Count}. " +
                $"Plan rules: [{string.Join(", ", plan.MemoizedRuleNames)}]. " +
                $"Expected: [{string.Join(", ", expectedCandidates)}].\nSource:\n{scenario.Source}");
        });
    }

    /// <summary>
    /// Builds a GrammarIR from a complete Storm grammar source (including the grammar header).
    /// </summary>
    private static GrammarIR BuildGrammarIRFromFullSource(string fullSource)
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = fullSource,
        };

        var resolver = new StormImportResolver(
            ValidationTestHelpers.InMemoryFileReader(files));
        var resolution = resolver.Resolve("root/main.storm");
        var grammar = StormGrammar.Build(resolution);
        var lowering = GrammarIRLowering.Lower(grammar);

        Assert.True(
            lowering.Succeeded,
            "Grammar lowering failed: " +
                string.Join(", ", lowering.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(lowering.Grammar);

        return lowering.Grammar!;
    }
}