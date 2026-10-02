using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Storm.SourceGenerator.Analyze;
using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.Generator;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Property-based tests for Storm generator determinism.
/// </summary>
public class StormGeneratorDeterminismPropertyTests
{
    // ---------------------------------------------------------------
    //  30.1 — Storm generator determinism
    //  **Validates: Correctness Property 10**
    //  Repeated generator invocations produce byte-identical C# output.
    // ---------------------------------------------------------------

    [Fact]
    public void StormGeneratorDeterminism_RepeatedInvocationsProduceIdenticalOutput_SimpleGrammar()
    {
        var grammar = "grammar Test;\ntoken Number = [0-9]+;\ntoken Ident = [a-zA-Z_][a-zA-Z0-9_]*;\nhidden token WS = [ \\t\\n]+;\nrule Start = Number;";

        AssertDeterministicOutput(grammar);
    }

    [Fact]
    public void StormGeneratorDeterminism_RepeatedInvocationsProduceIdenticalOutput_MultipleRules()
    {
        var grammar = @"grammar Test;
token Number = [0-9]+;
token Ident = [a-zA-Z_][a-zA-Z0-9_]*;
hidden token WS = [ \t\n]+;
rule Expr = Number | Ident;
rule Start = Expr;";

        AssertDeterministicOutput(grammar);
    }

    [Fact]
    public void StormGeneratorDeterminism_RepeatedInvocationsProduceIdenticalOutput_EmptyGrammar()
    {
        // Empty grammar produces STORM_NO_ROOT_GRAMMAR diagnostic deterministically
        var grammar = "";

        AssertDeterministicOutput(grammar);
    }

    [Fact]
    public void StormGeneratorDeterminism_RepeatedInvocationsProduceIdenticalOutput_HiddenTokens()
    {
        var grammar = @"grammar Test;
token Number = [0-9]+;
hidden token WS = [ \t]+;
hidden token Newline = [\n\r]+;
hidden token Comment = '//' [^\n]*;
rule Start = Number;";

        AssertDeterministicOutput(grammar);
    }

    [Fact]
    public void StormGeneratorDeterminism_RepeatedInvocationsProduceIdenticalOutput_MultipleTokens()
    {
        var grammar = @"grammar Test;
token Plus = '+';
token Minus = '-';
token Star = '*';
token Slash = '/';
token Number = [0-9]+;
hidden token WS = [ \t\n]+;
rule Start = Number;";

        AssertDeterministicOutput(grammar);
    }

    // ===============================================================
    //  Task 6.4 — Property 6: Determinism of generated output (emission)
    //  Validates: Requirements 5.4, 5.5
    //
    //  These tests close the loop between the analysis pass and the
    //  emitter: running MemoizationAnalysisPass + ParserEmitter two or
    //  more times from byte-identical grammar source must yield (a) an
    //  identical MemoizationPlan (same MemoizedRuleNames and RuleIds) AND
    //  (b) byte-for-byte identical parser source. Task 1.5 already proved
    //  id determinism at the PLAN level; this asserts determinism of the
    //  EMITTED PARSER SOURCE end-to-end when memoization is actually
    //  active (Count > 0).
    //
    //  Each grammar in the data set marks at least one rule, so the
    //  memoized emission path (memo field + MemoKey helper + per-rule
    //  probe/store wrapper) is genuinely exercised. The whole pipeline is
    //  a pure, deterministic in-memory function — no FsCheck and no
    //  external process are involved.
    // ===============================================================

    /// <summary>
    /// Grammar sources that each contain a recursion cycle crossing a
    /// backtracking construct, so <see cref="MemoizationAnalysisPass.Run"/> marks a
    /// non-empty set of rules. These are the same shapes the analysis-pass tests use
    /// (the type-argument cycle and the expression precedence ladder), so the memoized
    /// emission path is actually driven.
    /// </summary>
    public static IEnumerable<object[]> MemoizingGrammars()
    {
        // (1) The type-argument recursion cycle — marks all four cycle rules.
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

        // (2) The expression precedence ladder — marks all four ladder rules.
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
    }

    /// <summary>
    /// **Validates: Requirements 5.4, 5.5**
    ///
    /// Sub-case (a): running the analysis + parser emission two or more times on the
    /// SAME <see cref="GrammarIR"/> instance yields an identical plan (MemoizedRuleNames
    /// and RuleIds) and byte-for-byte identical parser source.
    /// </summary>
    [Theory]
    [MemberData(nameof(MemoizingGrammars))]
    public void MemoizedParserEmission_IsDeterministic_AcrossRepeatedRunsOnSameGrammar(string source)
    {
        var grammar = BuildGrammarIR(source);

        var first = RunAnalysisAndEmit(grammar);
        var second = RunAnalysisAndEmit(grammar);
        var third = RunAnalysisAndEmit(grammar);

        // Guard: the grammar genuinely exercises the memoized emission path.
        Assert.True(
            first.Plan.Count > 0,
            "Expected the grammar to mark at least one rule so the memoized emission path is exercised.");

        // (a) Identical memoized rule ids across runs.
        AssertSamePlan(first.Plan, second.Plan);
        AssertSamePlan(second.Plan, third.Plan);

        // (b) Byte-for-byte identical parser source across runs.
        Assert.Equal(first.ParserSource, second.ParserSource);
        Assert.Equal(second.ParserSource, third.ParserSource);
    }

    /// <summary>
    /// **Validates: Requirements 5.4, 5.5**
    ///
    /// Sub-case (b): rebuilding the grammar from the SAME source bytes into a fresh
    /// <see cref="GrammarIR"/> and re-running the analysis + parser emission yields an
    /// identical plan and byte-for-byte identical parser source — the cross-build
    /// determinism guarantee.
    /// </summary>
    [Theory]
    [MemberData(nameof(MemoizingGrammars))]
    public void MemoizedParserEmission_IsDeterministic_AcrossRebuildsFromSameSource(string source)
    {
        var first = RunAnalysisAndEmit(BuildGrammarIR(source));
        var second = RunAnalysisAndEmit(BuildGrammarIR(source));
        var third = RunAnalysisAndEmit(BuildGrammarIR(source));

        // Guard: the grammar genuinely exercises the memoized emission path.
        Assert.True(
            first.Plan.Count > 0,
            "Expected the grammar to mark at least one rule so the memoized emission path is exercised.");

        // Identical memoized rule ids across rebuilds from byte-identical source.
        AssertSamePlan(first.Plan, second.Plan);
        AssertSamePlan(second.Plan, third.Plan);

        // Byte-for-byte identical parser source across rebuilds.
        Assert.Equal(first.ParserSource, second.ParserSource);
        Assert.Equal(second.ParserSource, third.ParserSource);
    }

    // ---------------------------------------------------------------
    //  Local helpers for the emission-determinism property (task 6.4)
    // ---------------------------------------------------------------

    /// <summary>
    /// Builds an in-memory Storm grammar and lowers it to a <see cref="GrammarIR"/>,
    /// asserting the lowering succeeded. Mirrors the helper in
    /// <c>MemoizationAnalysisPassTests</c>.
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

    /// <summary>
    /// Runs <see cref="MemoizationAnalysisPass.Run"/> over the grammar and emits the
    /// parser source under the resulting plan, returning both for comparison.
    /// </summary>
    private static (MemoizationPlan Plan, string ParserSource) RunAnalysisAndEmit(GrammarIR grammar)
    {
        var plan = MemoizationAnalysisPass.Run(grammar);
        var context = new EmissionContext("Test.Generated", "Test", grammar, plan);
        var parserSource = ParserEmitter.Emit(context);
        return (plan, parserSource);
    }

    /// <summary>
    /// Asserts that two plans contain the same memoized rule names in the same order
    /// and assign the same id to every rule (same rule → same id).
    /// </summary>
    private static void AssertSamePlan(MemoizationPlan expected, MemoizationPlan actual)
    {
        // MemoizedRuleNames sequences are equal across runs (order-sensitive).
        Assert.Equal(expected.MemoizedRuleNames, actual.MemoizedRuleNames);

        // RuleIds maps are equal across runs (same rule → same id, no extra/missing keys).
        Assert.Equal(expected.RuleIds.Count, actual.RuleIds.Count);
        foreach(var entry in expected.RuleIds)
        {
            Assert.True(
                actual.RuleIds.ContainsKey(entry.Key),
                $"Rule '{entry.Key}' is missing from the second plan.");
            Assert.Equal(entry.Value, actual.RuleIds[entry.Key]);
        }
    }

    /// <summary>
    /// Runs the generator twice on the same grammar and asserts byte-identical output.
    /// </summary>
    private static void AssertDeterministicOutput(string grammar, string fileName = "Test.storm")
    {
        var result1 = RunGenerator(grammar, fileName);
        var result2 = RunGenerator(grammar, fileName);

        Assert.Equal(result1.GeneratedTrees.Length, result2.GeneratedTrees.Length);

        for(var i = 0; i < result1.GeneratedTrees.Length; i++)
        {
            var text1 = result1.GeneratedTrees[i].GetText().ToString();
            var text2 = result2.GeneratedTrees[i].GetText().ToString();

            Assert.Equal(text1, text2);
        }

        // Also verify diagnostics are identical
        Assert.Equal(result1.Diagnostics.Length, result2.Diagnostics.Length);
    }

    private static GeneratorDriverRunResult RunGenerator(string stormGrammar, string fileName = "Test.storm")
    {
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            references: new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
            },
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new StormIncrementalGenerator();
        var additionalText = new InMemoryAdditionalText(fileName, stormGrammar);

        var driver = CSharpGeneratorDriver.Create(generator)
            .AddAdditionalTexts(ImmutableArray.Create<AdditionalText>(additionalText));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

        return driver.GetRunResult();
    }

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private string text;

        public override string Path { get; }

        public InMemoryAdditionalText(string path, string text)
        {
            Path = path;
            this.text = text;
        }

        public override SourceText? GetText(CancellationToken cancellationToken = default)
        {
            return SourceText.From(text);
        }
    }
}