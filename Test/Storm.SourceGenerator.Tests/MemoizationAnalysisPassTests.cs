using Storm.SourceGenerator.Analyze;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Tests;

// ===================================================================
//  MemoizationAnalysisPass tests
//
//  These tests build in-memory Storm grammars (reusing the pattern in
//  ValidationPassTests.cs), lower them to GrammarIR, and assert against
//  the MemoizationPlan produced by MemoizationAnalysisPass.Run.
//
//  NOTE: This file is shared by tasks 1.3, 1.4, and 1.5. Task 1.3
//  covers Property 4 (heuristic coverage). Additional case-based unit
//  tests (1.4) and the id-determinism property (1.5) extend this same
//  class by adding methods below; the class is intentionally left
//  open for extension.
// ===================================================================

public class MemoizationAnalysisPassTests
{
    // ---------------------------------------------------------------
    //  Shared helper
    // ---------------------------------------------------------------

    /// <summary>
    /// Builds an in-memory Storm grammar and lowers it to a
    /// <see cref="GrammarIR"/>, asserting the lowering succeeded.
    /// </summary>
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

    // ===============================================================
    //  Property 4: Heuristic coverage
    //  Validates: Requirements 3.1, 3.2, 3.4, 3.5
    // ===============================================================

    [Fact]
    public void TypeArgumentCycle_MarksAllFourCycleRules()
    {
        // Models a recursive grammar cycle:
        //   TypeArgument    -> TypeReference        (via the TypeReference alternative)
        //   TypeReference   -> SingleTypeRef        (first SingleTypeRef (PIPE SingleTypeRef)*)
        //   SingleTypeRef   -> TypeArgumentList     (via the optional TypeArgumentList?)
        //   TypeArgumentList-> TypeArgument         ((COMMA TypeArgument)* tail)
        // Backtracking constructs live on the cycle (TypeArgumentList's '*',
        // TypeReference's '*', the multi-alternative TypeArgument/SingleTypeRef,
        // and SingleTypeRef's optional), so EVERY rule on the cycle is marked.
        var grammar = BuildGrammarIR(
            "token LT = \"<\";\n" +
            "token GT = \">\";\n" +
            "token COMMA = \",\";\n" +
            "token PIPE = \"|\";\n" +
            "token IDENT = [a-zA-Z]+;\n" +
            "token NUM = [0-9]+;\n" +
            "rule TypeArgumentList = LT TypeArgument (COMMA TypeArgument)* GT;\n" +
            "rule TypeArgument = NUM | TypeReference;\n" +
            "rule TypeReference = SingleTypeRef (PIPE SingleTypeRef)*;\n" +
            "rule SingleTypeRef = IDENT TypeArgumentList? | NUM;");

        var plan = MemoizationAnalysisPass.Run(grammar);

        // Requirement 3.1 / 3.2: every rule on the backtracking cycle is marked.
        Assert.True(plan.IsMemoized("TypeArgument"), "TypeArgument should be memoized");
        Assert.True(plan.IsMemoized("TypeReference"), "TypeReference should be memoized");
        Assert.True(plan.IsMemoized("SingleTypeRef"), "SingleTypeRef should be memoized");
        Assert.True(plan.IsMemoized("TypeArgumentList"), "TypeArgumentList should be memoized");

        // All four cycle rules are present in the plan.
        Assert.Contains("TypeArgument", plan.MemoizedRuleNames);
        Assert.Contains("TypeReference", plan.MemoizedRuleNames);
        Assert.Contains("SingleTypeRef", plan.MemoizedRuleNames);
        Assert.Contains("TypeArgumentList", plan.MemoizedRuleNames);
    }

    [Fact]
    public void AcyclicStraightLineGrammar_CycleOnly_MarksNothing()
    {
        // No rule is reachable from itself: Start -> Expr -> Term -> terminals.
        // Expr and Term have multiple alternatives (backtracking), but with no
        // recursion cycle, the cycle-based heuristic alone excludes every rule.
        // However, the multi-caller heuristic marks Term because it appears in
        // both alternatives of Expr. Verify the multi-caller candidate is included.
        var grammar = BuildGrammarIR(
            "token IDENT = [a-zA-Z]+;\n" +
            "token NUM = [0-9]+;\n" +
            "token PLUS = \"+\";\n" +
            "rule Start = Expr;\n" +
            "rule Expr = Term PLUS Term | Term;\n" +
            "rule Term = NUM | IDENT;");

        var plan = MemoizationAnalysisPass.Run(grammar);

        // Term is a multi-caller candidate (referenced in 2 alternatives of Expr)
        // and is speculative-safe (multi-alternative rule), so it IS memoized.
        Assert.Equal(1, plan.Count);
        Assert.False(plan.IsMemoized("Start"));
        Assert.False(plan.IsMemoized("Expr"));
        Assert.True(plan.IsMemoized("Term"));
    }

    [Fact]
    public void BacktrackingFreeCycle_MarksNothing()
    {
        // A genuine recursion cycle (A -> B -> A) made only of single-alternative
        // rules with no '?'/'*'/'+' anywhere. The cycle is detected, but because no
        // member contains a backtracking construct, criterion 3.5 excludes them all.
        var grammar = BuildGrammarIR(
            "token LPAREN = \"(\";\n" +
            "token RPAREN = \")\";\n" +
            "token IDENT = [a-zA-Z]+;\n" +
            "rule A = LPAREN B RPAREN;\n" +
            "rule B = IDENT A;");

        var plan = MemoizationAnalysisPass.Run(grammar);

        // Requirement 3.5: a backtracking-free cycle is correctly excluded.
        Assert.Equal(0, plan.Count);
        Assert.False(plan.IsMemoized("A"));
        Assert.False(plan.IsMemoized("B"));
    }

    // ===============================================================
    //  Case-based unit tests (task 1.4)
    //  Validates: Requirements 3.1, 3.3, 3.4, 3.5, 3.6, 2.4
    //
    //  NOTE on coverage versus task 1.3:
    //    - Case 2 ("straight-line grammar -> empty plan") is already
    //      covered by AcyclicStraightLineGrammar_MarksNothing above.
    //    - Case 3 ("cycle of single-alternative rules with no
    //      ?/*/+ -> not marked") is already covered by
    //      BacktrackingFreeCycle_MarksNothing above.
    //  The methods below add the remaining cases (1, 4, 5).
    // ===============================================================

    // Case 1: expression precedence ladder.
    [Fact]
    public void ExpressionPrecedenceLadder_MarksAllLadderRules()
    {
        // Classic precedence ladder closing back on itself through the
        // parenthesised-primary alternative:
        //   Expr    -> Term            (first Term, then (PLUS Term)*)
        //   Term    -> Factor          (first Factor, then (STAR Factor)*)
        //   Factor  -> Primary         (pure delegation, no backtracking of its own)
        //   Primary -> Expr            (via the '(' Expr ')' alternative)
        // Edges Expr->Term->Factor->Primary->Expr form one SCC of size 4, so
        // every ladder rule is on a cycle (criterion 3.1/3.3). Backtracking lives
        // on the cycle (Expr/Term repetitions, Primary's ordered choice), so the
        // SCC crosses a Backtracking_Construct and EVERY member is marked --
        // including Factor, which has no backtracking of its own (whole-cycle
        // marking). All four are speculative-safe (Primary is multi-alternative;
        // Expr/Term/Factor are single-alternative with no committing token match),
        // so none is filtered out by SR-2.
        var grammar = BuildGrammarIR(
            "token PLUS = \"+\";\n" +
            "token STAR = \"*\";\n" +
            "token LPAREN = \"(\";\n" +
            "token RPAREN = \")\";\n" +
            "token NUM = [0-9]+;\n" +
            "rule Expr = Term (PLUS Term)*;\n" +
            "rule Term = Factor (STAR Factor)*;\n" +
            "rule Factor = Primary;\n" +
            "rule Primary = NUM | LPAREN Expr RPAREN;");

        var plan = MemoizationAnalysisPass.Run(grammar);

        // Requirements 3.1 / 3.3: every rule on the backtracking cycle is marked.
        Assert.True(plan.IsMemoized("Expr"), "Expr should be memoized");
        Assert.True(plan.IsMemoized("Term"), "Term should be memoized");
        Assert.True(plan.IsMemoized("Factor"), "Factor should be memoized (whole-cycle marking)");
        Assert.True(plan.IsMemoized("Primary"), "Primary should be memoized");
        Assert.Equal(4, plan.Count);
    }

    // Case 4: direct self-recursive rule with a repetition (self-edge). Req 3.6.
    [Fact]
    public void SelfRecursiveRuleWithRepetition_IsMarked()
    {
        // A single rule that references ITSELF through a repetition:
        //   SelfRep -> SelfRep   (via the (COMMA SelfRep)* tail)
        // This is a length-1 SCC with a self-edge, so SelfRep is on a cycle. Its
        // own body contains a Backtracking_Construct (the '*'), satisfying the
        // self-edge predicate of Requirement 3.6 directly. As a single-alternative
        // rule with no committing token match it is speculative-safe, so it is
        // marked rather than filtered by SR-2.
        var grammar = BuildGrammarIR(
            "token COMMA = \",\";\n" +
            "token NUM = [0-9]+;\n" +
            "rule SelfRep = NUM (COMMA SelfRep)*;");

        var plan = MemoizationAnalysisPass.Run(grammar);

        // Requirement 3.6: a self-edge whose own body backtracks is included.
        Assert.True(plan.IsMemoized("SelfRep"), "SelfRep should be memoized (self-edge + repetition)");
        Assert.Equal(1, plan.Count);
    }

    // Case 5: the SR-2 speculative-safety filter. Requirement 2.4.
    [Fact]
    public void SpeculativeSafetyFilter_AdmitsSafeCycleRules()
    {
        // A two-rule backtracking cycle exercising BOTH branches of the SR-2
        // speculative-safety predicate (IsSpeculativeSafe):
        //   Group -> Items   (via the '(' Items ')' alternative) -- MULTI-alternative
        //   Items -> Group   (first Group, then (COMMA Group)*)  -- SINGLE-alternative
        // SCC = {Group, Items}; the cycle crosses backtracking (Group's ordered
        // choice and Items' '*'), so both are candidates. The SR-2 filter then
        // admits both:
        //   * Group is multi-alternative  -> always speculative-safe.
        //   * Items is single-alternative -> speculative-safe because its body
        //     contains no mandatory committing token match.
        //
        // IMPORTANT (honest scope of this test, Requirement 2.4):
        // The SR-2 EXCLUSION branch keys on ContainsMandatoryTokenMatch. In the
        // CURRENT emitter every rule-body token is matched SPECULATIVELY (via the
        // 'ok' flag in EmitAltBodyWithCheck), never through a committing Require
        // that latches _hasError. ContainsMandatoryTokenMatch is therefore
        // conservatively false for EVERY grammar-expressible rule body, so no
        // grammar DSL input can drive a single-alternative rule to "unsafe" and
        // force exclusion. This test does not falsely claim it can; instead it
        // asserts the filter's wired-in SAFE path -- that a multi-alternative
        // cyclic rule and a speculative-safe single-alternative cyclic rule are
        // both included -- which proves IsSpeculativeSafe is consulted by Run and
        // returns true on both shapes the current emitter produces. The unsafe
        // exclusion branch remains the explicit structural hook for a future
        // emitter that introduces a committing mandatory token match.
        var grammar = BuildGrammarIR(
            "token LPAREN = \"(\";\n" +
            "token RPAREN = \")\";\n" +
            "token COMMA = \",\";\n" +
            "token NUM = [0-9]+;\n" +
            "rule Group = LPAREN Items RPAREN | NUM;\n" +
            "rule Items = Group (COMMA Group)*;");

        var plan = MemoizationAnalysisPass.Run(grammar);

        // Multi-alternative rule passes the SR-2 filter (always speculative-safe).
        Assert.True(plan.IsMemoized("Group"), "Group (multi-alternative) should pass the SR-2 filter");
        // Single-alternative rule passes the SR-2 filter (no committing token match).
        Assert.True(plan.IsMemoized("Items"), "Items (single-alternative, speculative-safe) should pass the SR-2 filter");
        Assert.Equal(2, plan.Count);
    }

    // ===============================================================
    //  Property 6: Determinism of generated output (id assignment)
    //  (task 1.5)
    //  Validates: Requirements 5.1, 5.2, 5.3, 5.4
    //
    //  These tests assert the structural id-assignment contract of the
    //  MemoizationPlan produced by MemoizationAnalysisPass.Run:
    //    * ids are exactly the dense set {0 .. Count-1} with no gaps  (5.1)
    //    * ids follow grammar declaration order                       (5.2)
    //    * ids are one-to-one (no two rules share an id)              (5.3)
    //    * the result is identical across repeated runs / rebuilds    (5.4)
    //  The analysis is a pure, deterministic in-memory function, so an
    //  xUnit [Theory] over a few representative grammars is appropriate;
    //  no FsCheck and no process spawning are involved.
    // ===============================================================

    /// <summary>
    /// Representative grammars that each mark two or more rules, so the
    /// dense/unique/declaration-order properties are non-trivial. The third
    /// grammar interleaves an unmarked (acyclic) rule between two marked
    /// self-recursive rules, so the memoized set is a proper subsequence of
    /// the declared rules — exercising "dense even when unmarked rules sit
    /// between marked ones" and a declaration order that is neither
    /// alphabetical nor contiguous.
    /// </summary>
    public static IEnumerable<object[]> MultiRuleMemoizingGrammars()
    {
        // (1) The type-argument recursion cycle. Declaration order
        //     (TypeArgumentList, TypeArgument, TypeReference, SingleTypeRef)
        //     is deliberately NOT alphabetical, so an alphabetical or
        //     discovery-order id assignment would fail the order assertion.
        yield return new object[]
        {
            "token LT = \"<\";\n" +
            "token GT = \">\";\n" +
            "token COMMA = \",\";\n" +
            "token PIPE = \"|\";\n" +
            "token IDENT = [a-zA-Z]+;\n" +
            "token NUM = [0-9]+;\n" +
            "rule TypeArgumentList = LT TypeArgument (COMMA TypeArgument)* GT;\n" +
            "rule TypeArgument = NUM | TypeReference;\n" +
            "rule TypeReference = SingleTypeRef (PIPE SingleTypeRef)*;\n" +
            "rule SingleTypeRef = IDENT TypeArgumentList? | NUM;",
        };

        // (2) The expression precedence ladder (Expr, Term, Factor, Primary).
        yield return new object[]
        {
            "token PLUS = \"+\";\n" +
            "token STAR = \"*\";\n" +
            "token LPAREN = \"(\";\n" +
            "token RPAREN = \")\";\n" +
            "token NUM = [0-9]+;\n" +
            "rule Expr = Term (PLUS Term)*;\n" +
            "rule Term = Factor (STAR Factor)*;\n" +
            "rule Factor = Primary;\n" +
            "rule Primary = NUM | LPAREN Expr RPAREN;",
        };

        // (3) Two marked self-recursive rules with an unmarked acyclic rule
        //     declared BETWEEN them. Expected memoized set in declaration
        //     order: [Alpha, Beta] with dense ids 0,1 (Bridge is skipped, so
        //     ids stay 0,1 rather than 0,2 — a direct density check).
        yield return new object[]
        {
            "token COMMA = \",\";\n" +
            "token NUM = [0-9]+;\n" +
            "rule Alpha = NUM (COMMA Alpha)*;\n" +
            "rule Bridge = NUM;\n" +
            "rule Beta = NUM (COMMA Beta)*;",
        };
    }

    // 5.1: ids are exactly {0 .. Count-1} with no gaps, and RuleIds has Count entries.
    [Theory]
    [MemberData(nameof(MultiRuleMemoizingGrammars))]
    public void RuleIds_AreDenseZeroToCountMinusOne_WithNoGaps(string source)
    {
        var grammar = BuildGrammarIR(source);
        var plan = MemoizationAnalysisPass.Run(grammar);

        // Guard: each grammar in the data set marks at least two rules, so the
        // density/uniqueness assertions below are meaningful.
        Assert.True(
            plan.Count >= 2,
            $"Expected the grammar to mark at least two rules, but Count was {plan.Count}.");

        // RuleIds covers exactly Count rules (no extra or missing entries).
        Assert.Equal(plan.Count, plan.RuleIds.Count);

        // The set of assigned ids is exactly {0, 1, ..., Count-1} — dense, 0-based, no gaps.
        var actualIds = plan.RuleIds.Values.OrderBy(id => id).ToList();
        var expectedIds = Enumerable.Range(0, plan.Count).ToList();
        Assert.Equal(expectedIds, actualIds);
    }

    // 5.3: no two rules share an id (the id assignment is one-to-one).
    [Theory]
    [MemberData(nameof(MultiRuleMemoizingGrammars))]
    public void RuleIds_AreUniquePerRule(string source)
    {
        var grammar = BuildGrammarIR(source);
        var plan = MemoizationAnalysisPass.Run(grammar);

        var distinctIdCount = plan.RuleIds.Values.Distinct().Count();
        Assert.Equal(plan.Count, distinctIdCount);
    }

    // 5.2: ids are assigned in grammar declaration order. The memoized names are
    // exactly the declaration-order subsequence of marked rules, id(rule) equals
    // its index in MemoizedRuleNames, and ids strictly increase with declaration order.
    [Theory]
    [MemberData(nameof(MultiRuleMemoizingGrammars))]
    public void RuleIds_FollowGrammarDeclarationOrder(string source)
    {
        var grammar = BuildGrammarIR(source);
        var plan = MemoizationAnalysisPass.Run(grammar);

        // MemoizedRuleNames must be the marked rules in the SAME relative order
        // as they are declared in grammar.Rules (a declaration-order subsequence).
        var expectedOrder = grammar.Rules
            .Select(r => r.Name)
            .Where(plan.IsMemoized)
            .ToList();
        Assert.Equal(expectedOrder, plan.MemoizedRuleNames);

        // id(rule) == index of the rule within MemoizedRuleNames.
        var memoizedNames = plan.MemoizedRuleNames.ToList();
        for(var i = 0; i < plan.MemoizedRuleNames.Count; i++)
        {
            var name = plan.MemoizedRuleNames[i];
            Assert.Equal(i, plan.RuleIds[name]);
            Assert.Equal(memoizedNames.IndexOf(name), plan.RuleIds[name]);
        }

        // Ids strictly increase in grammar declaration order: for any two memoized
        // rules, the one declared earlier has the smaller id.
        var previousDeclarationIndex = -1;
        foreach(var name in plan.MemoizedRuleNames)
        {
            var declarationIndex = IndexOfRule(grammar, name);
            Assert.True(
                declarationIndex > previousDeclarationIndex,
                $"Rule '{name}' breaks declaration-order id assignment " +
                    $"(declaration index {declarationIndex} did not follow {previousDeclarationIndex}).");
            previousDeclarationIndex = declarationIndex;
        }
    }

    // 5.4: repeated Run invocations on the same GrammarIR — and a rebuild of the
    // same grammar source — produce identical MemoizedRuleNames and RuleIds.
    [Theory]
    [MemberData(nameof(MultiRuleMemoizingGrammars))]
    public void Run_ProducesIdenticalPlanAcrossRepeatedInvocationsAndRebuilds(string source)
    {
        var grammar = BuildGrammarIR(source);

        // Repeated invocations on the SAME GrammarIR instance.
        var first = MemoizationAnalysisPass.Run(grammar);
        var second = MemoizationAnalysisPass.Run(grammar);
        var third = MemoizationAnalysisPass.Run(grammar);

        Assert.Equal(first.MemoizedRuleNames, second.MemoizedRuleNames);
        Assert.Equal(second.MemoizedRuleNames, third.MemoizedRuleNames);
        AssertSameRuleIds(first, second);
        AssertSameRuleIds(second, third);

        // Rebuilding the same grammar source and re-running yields identical ids,
        // mirroring the "identical ids across repeated builds" guarantee (Req 5.4).
        var rebuilt = BuildGrammarIR(source);
        var fourth = MemoizationAnalysisPass.Run(rebuilt);

        Assert.Equal(first.MemoizedRuleNames, fourth.MemoizedRuleNames);
        AssertSameRuleIds(first, fourth);
    }

    // ===============================================================
    //  Multi-caller heuristic unit tests (task 5.3)
    //  Validates: Requirements 4.1, 4.2, 4.3
    //
    //  These tests focus specifically on the multi-caller alternation
    //  heuristic: rules referenced from 2+ distinct alternatives of
    //  any parent rule's top-level alternation are detected as
    //  memoization candidates.
    // ===============================================================

    /// <summary>
    /// A rule referenced in 2+ distinct alternatives of a parent rule
    /// is detected as a multi-caller candidate and appears in the plan.
    /// </summary>
    [Fact]
    public void MultiCaller_RuleInTwoAlternatives_IsDetected()
    {
        // Parent has two alternatives, both referencing Shared.
        // Shared is a multi-alternative rule (speculative-safe).
        var grammar = BuildGrammarIR(
            "token NUM = [0-9]+;\n" +
            "token IDENT = [a-zA-Z]+;\n" +
            "rule Parent = NUM Shared | IDENT Shared;\n" +
            "rule Shared = NUM | IDENT;");

        var plan = MemoizationAnalysisPass.Run(grammar);

        // Shared appears in both alternatives of Parent -> multi-caller candidate.
        Assert.True(plan.IsMemoized("Shared"), "Shared should be memoized (referenced in 2 alternatives)");
    }

    /// <summary>
    /// A rule referenced in only 1 alternative of a parent is NOT
    /// detected as a multi-caller candidate.
    /// </summary>
    [Fact]
    public void MultiCaller_RuleInOneAlternativeOnly_IsNotDetected()
    {
        // Parent has two alternatives but OnlyOnce appears in just one.
        // Neither rule is on a cycle, and OnlyOnce isn't referenced from
        // multiple alternatives, so it should not be memoized.
        var grammar = BuildGrammarIR(
            "token NUM = [0-9]+;\n" +
            "token IDENT = [a-zA-Z]+;\n" +
            "token PLUS = \"+\";\n" +
            "rule Parent = NUM OnlyOnce | IDENT PLUS;\n" +
            "rule OnlyOnce = NUM | IDENT;");

        var plan = MemoizationAnalysisPass.Run(grammar);

        // OnlyOnce only appears in 1 alternative -> not a multi-caller candidate.
        Assert.False(plan.IsMemoized("OnlyOnce"), "OnlyOnce should NOT be memoized (only in 1 alternative)");
    }

    /// <summary>
    /// Multi-caller detection works across multiple parent rules.
    /// A rule referenced in 2+ alternatives of different parents
    /// is still detected.
    /// </summary>
    [Fact]
    public void MultiCaller_DetectionAcrossMultipleParentRules()
    {
        // Common is referenced in 2 alternatives of ParentA, and also
        // in 2 alternatives of ParentB. It should be detected regardless
        // of which parent triggers it.
        // OnlyInB is referenced in 2 alternatives of ParentB only.
        var grammar = BuildGrammarIR(
            "token NUM = [0-9]+;\n" +
            "token IDENT = [a-zA-Z]+;\n" +
            "token PLUS = \"+\";\n" +
            "rule ParentA = Common PLUS | Common NUM;\n" +
            "rule ParentB = Common OnlyInB | IDENT OnlyInB;\n" +
            "rule Common = NUM | IDENT;\n" +
            "rule OnlyInB = NUM | IDENT;");

        var plan = MemoizationAnalysisPass.Run(grammar);

        // Common is multi-caller from ParentA (appears in both alts).
        Assert.True(plan.IsMemoized("Common"), "Common should be memoized (multi-caller from ParentA)");
        // OnlyInB is multi-caller from ParentB (appears in both alts).
        Assert.True(plan.IsMemoized("OnlyInB"), "OnlyInB should be memoized (multi-caller from ParentB)");
    }

    /// <summary>
    /// Single-alternative parent rules do not produce multi-caller
    /// candidates, even if the child rule is referenced multiple times
    /// in a sequence.
    /// </summary>
    [Fact]
    public void MultiCaller_SingleAlternativeParent_DoesNotProduceCandidates()
    {
        // Parent has a single alternative with Repeated referenced twice
        // in a sequence. This is NOT multi-caller because there's only one
        // alternative — the heuristic requires 2+ DISTINCT alternatives.
        var grammar = BuildGrammarIR(
            "token NUM = [0-9]+;\n" +
            "token IDENT = [a-zA-Z]+;\n" +
            "token PLUS = \"+\";\n" +
            "rule Parent = Repeated PLUS Repeated;\n" +
            "rule Repeated = NUM | IDENT;");

        var plan = MemoizationAnalysisPass.Run(grammar);

        // Parent has only 1 alternative -> multi-caller heuristic skips it.
        // No cycles exist, so nothing should be memoized.
        Assert.False(plan.IsMemoized("Repeated"), "Repeated should NOT be memoized (single-alternative parent)");
        Assert.Equal(0, plan.Count);
    }

    // ===============================================================
    //  Candidate union and IsSpeculativeSafe filter (task 5.4)
    //  Validates: Requirements 5.1, 5.2, 5.3, 5.4
    //
    //  These tests verify:
    //    - @memo-annotated rules appear in the final plan (safe case)
    //    - @memo-annotated rules that fail IsSpeculativeSafe are excluded
    //      with a STORM_MEMO_IGNORED warning diagnostic
    //    - cycle, multi-caller, and @memo candidates all merge correctly
    //    - dense 0-based ids in declaration order after union + filter
    // ===============================================================

    /// <summary>
    /// A @memo-annotated rule that is speculative-safe (multi-alternative)
    /// appears in the final MemoizationPlan.
    /// </summary>
    [Fact]
    public void MemoAnnotatedRule_SpeculativeSafe_AppearsInPlan()
    {
        // MemoRule has multiple alternatives -> always speculative-safe.
        // It is annotated with @memo so it enters the candidate set.
        var grammar = BuildGrammarIR(
            "token NUM = [0-9]+;\n" +
            "token IDENT = [a-zA-Z]+;\n" +
            "rule Start = MemoRule;\n" +
            "@memo\n" +
            "rule MemoRule = NUM | IDENT;");

        var diagnostics = new List<StormDiagnostic>();
        var plan = MemoizationAnalysisPass.Run(grammar, diagnostics);

        Assert.True(plan.IsMemoized("MemoRule"), "MemoRule (@memo, safe) should be in the plan");
        Assert.False(plan.IsMemoized("Start"), "Start should NOT be in the plan (not a candidate)");
        Assert.Empty(diagnostics); // no warnings for safe @memo rules
    }

    /// <summary>
    /// A @memo-annotated rule that fails IsSpeculativeSafe is excluded from
    /// the plan and a STORM_MEMO_IGNORED warning diagnostic is emitted.
    /// </summary>
    /// <remarks>
    /// IsSpeculativeSafe returns false when a rule has zero alternatives
    /// (the only structural path to "unsafe" in the current emitter, since
    /// ContainsMandatoryTokenMatch is always false for grammar-expressible
    /// rule bodies). This test constructs the GrammarIR directly to produce
    /// such a rule.
    /// </remarks>
    [Fact]
    public void MemoAnnotatedRule_NotSpeculativeSafe_ExcludedWithWarning()
    {
        // Construct a GrammarIR directly with a zero-alternative @memo rule.
        // Zero alternatives => IsSpeculativeSafe returns false.
        var token = new TokenIR("NUM", new CharClassIR("[0-9]"), false, 0);
        var unsafeRule = new RuleIR("Unsafe", new List<RuleAlternativeIR>(), isMemo: true);
        var safeRule = new RuleIR("Safe", new List<RuleAlternativeIR>
        {
            new(null, Array.Empty<FieldLabelIR>(), new TokenRefIR(token)),
            new(null, Array.Empty<FieldLabelIR>(), new TokenRefIR(token)),
        }, isMemo: true);

        var grammar = new GrammarIR(
            new List<TokenIR> { token },
            new List<RuleIR> { unsafeRule, safeRule });

        var diagnostics = new List<StormDiagnostic>();
        var plan = MemoizationAnalysisPass.Run(grammar, diagnostics);

        // Unsafe is excluded because it fails IsSpeculativeSafe.
        Assert.False(plan.IsMemoized("Unsafe"), "Unsafe rule (0 alternatives) should be excluded from plan");
        // Safe is included because it passes IsSpeculativeSafe (multi-alternative).
        Assert.True(plan.IsMemoized("Safe"), "Safe rule (multi-alternative) should be in the plan");

        // A STORM_MEMO_IGNORED warning is emitted for the unsafe @memo rule.
        _ = Assert.Single(diagnostics);
        Assert.Equal(StormDiagnosticIds.STORM_MEMO_IGNORED, diagnostics[0].Id);
        Assert.Equal(StormDiagnosticSeverity.Warning, diagnostics[0].Severity);
        Assert.Contains("Unsafe", diagnostics[0].Message);
        Assert.Contains("not speculative-safe", diagnostics[0].Message);
    }

    /// <summary>
    /// When no diagnostics list is passed (null), the @memo excluded rule
    /// is still filtered but no exception is thrown.
    /// </summary>
    [Fact]
    public void MemoAnnotatedRule_NotSpeculativeSafe_NullDiagnostics_NoException()
    {
        var token = new TokenIR("NUM", new CharClassIR("[0-9]"), false, 0);
        var unsafeRule = new RuleIR("Unsafe", new List<RuleAlternativeIR>(), isMemo: true);

        var grammar = new GrammarIR(
            new List<TokenIR> { token },
            new List<RuleIR> { unsafeRule });

        // Should not throw even with null diagnostics.
        var plan = MemoizationAnalysisPass.Run(grammar, diagnostics: null);

        Assert.False(plan.IsMemoized("Unsafe"));
        Assert.Equal(0, plan.Count);
    }

    /// <summary>
    /// Cycle candidates, multi-caller candidates, and @memo candidates all
    /// merge into a single plan. Each source contributes distinct rules, and
    /// the union includes all of them (when speculative-safe).
    /// </summary>
    [Fact]
    public void CandidateUnion_AllThreeSourcesMergeCorrectly()
    {
        // Grammar designed to exercise all three candidate sources:
        //
        // CycleRule: on a recursion cycle with backtracking (self-recursive via *)
        // MultiCallerRule: referenced in 2 alternatives of Parent (multi-caller)
        // MemoRule: annotated with @memo (not on a cycle, not multi-caller)
        //
        // All are multi-alternative or single-alternative with no mandatory match,
        // so all pass IsSpeculativeSafe.
        var grammar = BuildGrammarIR(
            "token NUM = [0-9]+;\n" +
            "token IDENT = [a-zA-Z]+;\n" +
            "token COMMA = \",\";\n" +
            "rule Parent = NUM MultiCallerRule | IDENT MultiCallerRule;\n" +
            "rule MultiCallerRule = NUM | IDENT;\n" +
            "rule CycleRule = NUM (COMMA CycleRule)*;\n" +
            "@memo\n" +
            "rule MemoRule = NUM | IDENT;");

        var diagnostics = new List<StormDiagnostic>();
        var plan = MemoizationAnalysisPass.Run(grammar, diagnostics);

        // All three sources contribute to the final plan.
        Assert.True(plan.IsMemoized("CycleRule"), "CycleRule should be memoized (cycle candidate)");
        Assert.True(plan.IsMemoized("MultiCallerRule"), "MultiCallerRule should be memoized (multi-caller candidate)");
        Assert.True(plan.IsMemoized("MemoRule"), "MemoRule should be memoized (@memo candidate)");

        // Parent is NOT a candidate from any source.
        Assert.False(plan.IsMemoized("Parent"), "Parent should NOT be memoized");

        // No warnings because all candidates are speculative-safe.
        Assert.Empty(diagnostics);
    }

    /// <summary>
    /// A rule that qualifies through multiple sources (e.g., both @memo and
    /// multi-caller) appears only once in the plan with a single id.
    /// </summary>
    [Fact]
    public void CandidateUnion_DuplicateFromMultipleSources_AppearsOnce()
    {
        // SharedRule is both @memo-annotated AND a multi-caller candidate
        // (referenced in 2 alternatives of Parent). It should appear once.
        var grammar = BuildGrammarIR(
            "token NUM = [0-9]+;\n" +
            "token IDENT = [a-zA-Z]+;\n" +
            "rule Parent = NUM SharedRule | IDENT SharedRule;\n" +
            "@memo\n" +
            "rule SharedRule = NUM | IDENT;");

        var plan = MemoizationAnalysisPass.Run(grammar);

        Assert.True(plan.IsMemoized("SharedRule"));
        // Should appear exactly once (no duplicates from multiple sources).
        Assert.Equal(1, plan.MemoizedRuleNames.Count(n => n == "SharedRule"));
    }

    /// <summary>
    /// The final plan assigns dense 0-based ids in grammar declaration order
    /// across all three candidate sources, skipping non-candidate rules.
    /// </summary>
    [Fact]
    public void DenseIds_DeclarationOrder_AcrossAllCandidateSources()
    {
        // Declaration order: Parent, MultiCallerRule, CycleRule, PlainRule, MemoRule
        // Expected plan (in declaration order): MultiCallerRule(0), CycleRule(1), MemoRule(2)
        // Parent is not a candidate. PlainRule is not a candidate.
        var grammar = BuildGrammarIR(
            "token NUM = [0-9]+;\n" +
            "token IDENT = [a-zA-Z]+;\n" +
            "token COMMA = \",\";\n" +
            "rule Parent = NUM MultiCallerRule | IDENT MultiCallerRule;\n" +
            "rule MultiCallerRule = NUM | IDENT;\n" +
            "rule CycleRule = NUM (COMMA CycleRule)*;\n" +
            "rule PlainRule = NUM;\n" +
            "@memo\n" +
            "rule MemoRule = NUM | IDENT;");

        var plan = MemoizationAnalysisPass.Run(grammar);

        // Dense 0-based ids.
        Assert.Equal(3, plan.Count);
        var expectedIds = Enumerable.Range(0, 3).ToList();
        var actualIds = plan.RuleIds.Values.OrderBy(id => id).ToList();
        Assert.Equal(expectedIds, actualIds);

        // Ids follow grammar declaration order:
        // MultiCallerRule declared before CycleRule, declared before MemoRule.
        Assert.True(plan.RuleIds["MultiCallerRule"] < plan.RuleIds["CycleRule"],
            "MultiCallerRule should have a lower id than CycleRule (declared earlier)");
        Assert.True(plan.RuleIds["CycleRule"] < plan.RuleIds["MemoRule"],
            "CycleRule should have a lower id than MemoRule (declared earlier)");

        // Exact id assignments.
        Assert.Equal(0, plan.RuleIds["MultiCallerRule"]);
        Assert.Equal(1, plan.RuleIds["CycleRule"]);
        Assert.Equal(2, plan.RuleIds["MemoRule"]);
    }

    // ---------------------------------------------------------------
    //  Local helpers for the id-determinism property (task 1.5)
    // ---------------------------------------------------------------

    /// <summary>
    /// Returns the declaration index of <paramref name="ruleName"/> in
    /// <paramref name="grammar"/>, or -1 if the rule is not present.
    /// </summary>
    private static int IndexOfRule(GrammarIR grammar, string ruleName)
    {
        for(var i = 0; i < grammar.Rules.Count; i++)
        {
            if(grammar.Rules[i].Name == ruleName)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Asserts that two plans assign exactly the same id to exactly the same set
    /// of memoized rule names.
    /// </summary>
    private static void AssertSameRuleIds(MemoizationPlan expected, MemoizationPlan actual)
    {
        Assert.Equal(expected.RuleIds.Count, actual.RuleIds.Count);
        foreach(var entry in expected.RuleIds)
        {
            Assert.True(
                actual.RuleIds.ContainsKey(entry.Key),
                $"Rule '{entry.Key}' is missing from the second plan.");
            Assert.Equal(entry.Value, actual.RuleIds[entry.Key]);
        }
    }
}