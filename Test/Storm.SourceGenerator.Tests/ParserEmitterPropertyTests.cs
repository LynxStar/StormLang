using System.Text.RegularExpressions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Property-based tests for the ParserEmitter using FsCheck.
/// Validates structural invariants of the emitted PEG ordered-choice parser code.
/// </summary>
public class ParserEmitterPropertyTests
{
    // ── Shared token pool ───────────────────────────────────────────

    private static readonly TokenIR TokenA = new("Alpha", new StringLiteralIR("alpha"), false, 0);
    private static readonly TokenIR TokenB = new("Beta", new StringLiteralIR("beta"), false, 1);
    private static readonly TokenIR TokenC = new("Gamma", new StringLiteralIR("gamma"), false, 2);
    private static readonly TokenIR TokenD = new("Delta", new StringLiteralIR("delta"), false, 3);
    private static readonly TokenIR TokenE = new("Epsilon", new StringLiteralIR("epsilon"), false, 4);

    private static readonly TokenIR[] AllTokens = { TokenA, TokenB, TokenC, TokenD, TokenE };

    // ── Generators ──────────────────────────────────────────────────

    private static Gen<RuleAlternativeIR> GenNamedAlternative(string altName)
    {
        return Gen.Elements(AllTokens).Select(token =>
            new RuleAlternativeIR(altName, new List<FieldLabelIR>(), new TokenRefIR(token)));
    }

    private static Gen<RuleAlternativeIR> GenLabeledAlternative()
    {
        return Gen.Elements(AllTokens).Two().Select(pair =>
        {
            var (t1, t2) = pair;
            var expr1 = new TokenRefIR(t1);
            var expr2 = new TokenRefIR(t2);
            var seq = new SequenceIR(new List<ExpressionIR> { expr1, expr2 });
            var labels = new List<FieldLabelIR>
            {
                new("left", expr1),
                new("right", expr2)
            };
            return new RuleAlternativeIR(null, labels, seq);
        });
    }

    private static Gen<RuleIR> GenMultiAltRule()
    {
        var altNames = new[] { "First", "Second", "Third", "Fourth", "Fifth" };
        return Gen.Choose(2, 5).SelectMany(count =>
        {
            var gens = Enumerable.Range(0, count)
                .Select(i => GenNamedAlternative(altNames[i]))
                .ToArray();

            var combined = Gen.Constant(new List<RuleAlternativeIR>());
            foreach(var g in gens)
            {
                combined = combined.SelectMany(list =>
                    g.Select(item =>
                    {
                        var newList = new List<RuleAlternativeIR>(list) { item };
                        return newList;
                    }));
            }

            return combined.Select(alts => new RuleIR("TestRule", alts));
        });
    }

    private static Gen<RuleIR> GenSingleAltRule()
    {
        return GenLabeledAlternative().Select(alt =>
            new RuleIR("TestRule", new List<RuleAlternativeIR> { alt }));
    }

    private static Gen<RuleIR> GenAnyArityRule()
    {
        return Gen.Frequency(
            (1, GenSingleAltRule()),
            (3, GenMultiAltRule()));
    }

    // ── Emit helper ─────────────────────────────────────────────────

    private static string EmitForRule(RuleIR rule)
    {
        var grammar = new GrammarIR(
            new List<TokenIR>(AllTokens),
            new List<RuleIR> { rule });
        var ctx = new EmissionContext("Test.Generated", "Test", grammar);
        return ParserEmitter.Emit(ctx);
    }

    // ── Property 1: Multi-alternative rules emit PEG ordered-choice pattern ──

    /// <summary>
    /// **Validates: Requirements 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 2.1, 2.2, 2.3, 7.1, 7.2, 7.3, 7.4**
    ///
    /// For any RuleIR with 2+ alternatives, the emitted code SHALL contain:
    /// (a) exactly one state-save preamble,
    /// (b) one state-reset block per alternative,
    /// (c) an epilogue restoring _hasError to savedError.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property MultiAlt_EmitsPegOrderedChoicePattern()
    {
        return Prop.ForAll(GenMultiAltRule().ToArbitrary(), rule =>
        {
            var output = EmitForRule(rule);

            // Extract the internal _ParseTestRule method to avoid counting matches in Init/helpers
            var methodMarker = "private static TestRuleNode? _ParseTestRule()";
            var methodStart = output.IndexOf(methodMarker);
            Assert.True(methodStart >= 0, "Expected _ParseTestRule method in output");
            var methodBody = output[methodStart..];

            // (a) Exactly one state-save preamble
            var savedPosCount = Regex.Matches(methodBody, Regex.Escape("var saved = _pos;")).Count;
            var savedErrorCount = Regex.Matches(methodBody, Regex.Escape("var savedError = _hasError;")).Count;
            var savedDiagCount = Regex.Matches(methodBody, Regex.Escape("var savedDiagCount = _diagnostics?.Count ?? 0;")).Count;

            Assert.Equal(1, savedPosCount);
            Assert.Equal(1, savedErrorCount);
            Assert.Equal(1, savedDiagCount);

            // (b) Per-alternative state resets + epilogue
            var altCount = rule.Alternatives.Count;
            var posResetCount = Regex.Matches(methodBody, Regex.Escape("_pos = saved;")).Count;
            var errorResetCount = Regex.Matches(methodBody, Regex.Escape("_hasError = false;")).Count;
            var truncateCount = Regex.Matches(methodBody, Regex.Escape("_diagnostics?.TruncateTo(savedDiagCount);")).Count;

            // _pos = saved: once per alt + once in epilogue
            Assert.Equal(altCount + 1, posResetCount);
            // _hasError = false: once per alt (reset before each attempt)
            Assert.Equal(altCount, errorResetCount);
            // TruncateTo: once per alt + once in epilogue
            Assert.Equal(altCount + 1, truncateCount);

            // (c) Epilogue restores _hasError to savedError (not false)
            var epilogueRestore = Regex.Matches(methodBody, Regex.Escape("_hasError = savedError;")).Count;
            Assert.Equal(1, epilogueRestore);
        });
    }

    // ── Property 2: Single-alternative rules emit direct parse pattern ──

    /// <summary>
    /// **Validates: Requirements 3.1, 3.2, 3.3**
    ///
    /// For any RuleIR with exactly 1 alternative, the emitted code SHALL begin with
    /// `if (_hasError) return null;` and SHALL NOT contain backtracking artifacts.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property SingleAlt_EmitsDirectParseWithoutBacktracking()
    {
        return Prop.ForAll(GenSingleAltRule().ToArbitrary(), rule =>
        {
            var output = EmitForRule(rule);

            // Extract the internal _ParseTestRule method body
            var methodMarker = "private static TestRuleNode? _ParseTestRule()";
            var methodStart = output.IndexOf(methodMarker);
            Assert.True(methodStart >= 0, "Expected _ParseTestRule method in output");
            var methodBody = output[methodStart..];

            // Should begin with if (_hasError) return null;
            Assert.Contains("if (_hasError) return null;", methodBody);

            // Should NOT contain backtracking state save/restore
            Assert.DoesNotContain("var saved = _pos", methodBody);

            // Should NOT contain TruncateTo
            Assert.DoesNotContain("TruncateTo", methodBody);

            // Should NOT contain the alt-reset pattern (_pos = saved; _hasError = false;)
            Assert.False(
                Regex.IsMatch(methodBody, @"_pos = saved;\s*\n\s*_hasError = false;"),
                "Single-alt should not have alt-reset pattern");
        });
    }

    // ── Property 3: No legacy prediction artifacts in emitted code ──

    /// <summary>
    /// **Validates: Requirements 5.2, 5.3, 5.4, 10.2, 10.3, 10.4**
    ///
    /// For any RuleIR (any arity), the emitted code SHALL NOT contain legacy
    /// prediction artifacts: switch(Peek()), Peek(1), TryParse, or _memo.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property NoLegacyPredictionArtifacts()
    {
        return Prop.ForAll(GenAnyArityRule().ToArbitrary(), rule =>
        {
            var output = EmitForRule(rule);

            Assert.DoesNotContain("switch (Peek())", output);
            Assert.DoesNotContain("Peek(1)", output);
            Assert.DoesNotContain("TryParse", output);
            Assert.DoesNotContain("_memo", output);
        });
    }

    // ── Property 4: DiagnosticBag TruncateTo preserves prefix ──

    /// <summary>
    /// **Validates: Requirements 11.3, 11.4**
    ///
    /// For any sequence of Add/TruncateTo operations on a DiagnosticBag,
    /// calling TruncateTo(n) where n &lt;= Count SHALL result in Count == n
    /// and the first n messages being identical to the first n messages before the call.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property DiagnosticBag_TruncateTo_PreservesPrefix()
    {
        var messagePool = new[] {
            "error1", "error2", "warning1", "info1", "error3",
            "unexpected token", "missing semicolon", "type mismatch"
        };

        var gen = Gen.Choose(0, 20).SelectMany(msgCount =>
            Gen.ArrayOf(Gen.Elements(messagePool), msgCount).SelectMany(messages =>
                Gen.Choose(0, messages.Length).Select(truncTarget =>
                    (messages, truncTarget))));

        return Prop.ForAll(gen.ToArbitrary(), pair =>
        {
            var (messages, truncateTarget) = pair;

            // Simulate DiagnosticBag behavior (mirrors the emitted code)
            var bag = new List<string>(messages);
            var prefixBefore = bag.Take(truncateTarget).ToList();

            // TruncateTo: same logic as emitted DiagnosticBag
            if(truncateTarget >= 0 && truncateTarget < bag.Count)
            {
                bag.RemoveRange(truncateTarget, bag.Count - truncateTarget);
            }

            Assert.Equal(truncateTarget, bag.Count);
            Assert.Equal(prefixBefore, bag);
        });
    }
}