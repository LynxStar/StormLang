using System.Collections.Immutable;
using System.Reflection;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Storm.SourceGenerator.Analyze;
using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// In-process emit/inspect tests for the selective packrat memoization code shape
/// emitted by <see cref="ParserEmitter"/>. Follows the same in-memory grammar →
/// lower → <see cref="EmissionContext"/> → emit-to-string pattern used by
/// <c>EmitterTests</c> and <c>ParserEmitterDiagnosticTests</c>. No external process
/// is spawned.
/// </summary>
/// <remarks>
/// This class is the shared home for the emitter-side memoization properties:
/// <list type="bullet">
///   <item>Property 1 — empty-plan byte-identical output (task 4.3, implemented here).</item>
///   <item>Property — memoized wrapper shape (task 4.4, implemented here).</item>
///   <item>Property 2 — packrat position purity / SR-1 (task 4.5, implemented here).</item>
///   <item>Property 3 — cached-failure diagnostic neutrality / SR-2 (task 4.6, implemented here).</item>
/// </list>
/// The shared <see cref="EmitParserSource"/> helper exists so later tasks can emit a
/// parser from a grammar plus an arbitrary <see cref="MemoizationPlan"/> without
/// duplicating the lowering boilerplate. Task 4.5 adds an in-process compile-and-drive
/// harness (<see cref="CompileParser"/>) that compiles the emitted runtime + AST +
/// lexer + parser into an in-memory assembly via Roslyn and drives it through
/// reflection. No external process is spawned — Roslyn in-memory compilation plus
/// reflection is entirely in-process.
/// </remarks>
public class MemoizationEmitterTests
{
    // ---------------------------------------------------------------
    //  Helpers (shared with later tasks 4.4/4.5/4.6)
    // ---------------------------------------------------------------

    private static Func<string, string?> InMemoryFileReader(Dictionary<string, string> files)
    {
        return path =>
        {
            var normalized = StormImportResolver.CanonicalizePath(path);
            if(files.TryGetValue(normalized, out var contents))
            {
                return contents;
            }

            return null;
        };
    }

    /// <summary>
    /// Lowers the supplied grammar body into a <see cref="GrammarIR"/>, mirroring the
    /// helper used in <c>EmitterTests</c>/<c>ParserEmitterDiagnosticTests</c>.
    /// </summary>
    private static GrammarIR LowerGrammar(string grammar, string grammarName)
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar " + grammarName + ";\n" + grammar
        };

        var resolver = new StormImportResolver(InMemoryFileReader(files));
        var resolution = resolver.Resolve("root/main.storm");
        var stormGrammar = StormGrammar.Build(resolution);
        var loweringResult = GrammarIRLowering.Lower(stormGrammar);

        Assert.True(loweringResult.Succeeded, "Grammar lowering failed: " +
            string.Join(", ", loweringResult.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(loweringResult.Grammar);

        return loweringResult.Grammar!;
    }

    /// <summary>
    /// Emits the parser source for a grammar under a specific memoization plan.
    /// When <paramref name="plan"/> is null, the <see cref="EmissionContext"/> is
    /// constructed WITHOUT a memoization argument so it defaults to
    /// <see cref="MemoizationPlan.Empty"/> (the "no-plan" path). When a plan is
    /// supplied it is passed explicitly.
    /// </summary>
    private static string EmitParserSource(
        string grammar,
        MemoizationPlan? plan = null,
        string grammarName = "Test",
        string ns = "Test.Generated")
    {
        var grammarIr = LowerGrammar(grammar, grammarName);

        // Null => omit the constructor arg entirely (defaults to Empty).
        // Non-null => pass it explicitly.
        var context = plan is null
            ? new EmissionContext(ns, grammarName, grammarIr)
            : new EmissionContext(ns, grammarName, grammarIr, plan);

        return ParserEmitter.Emit(context);
    }

    // A representative grammar with multiple rules, ordered choice, an optional,
    // and a repetition — i.e. the kinds of constructs a real memoization plan
    // would target. For an empty plan none of that matters to the output, which
    // is exactly the property under test.
    private const string RepresentativeGrammar =
        "token Number = [0-9]+;\n" +
        "token Ident = [a-z]+;\n" +
        "rule Expr = Number as Lit | Ident as Var;\n" +
        "rule Args = Expr (\",\" Expr)*;\n" +
        "rule Call = Ident \"(\" Args? \")\";\n" +
        "rule Start = Call;";

    // ---------------------------------------------------------------
    //  Task 4.3 — Property 1: Output preservation (empty-plan emission)
    //  Validates: Requirements 1.5
    // ---------------------------------------------------------------

    [Fact]
    public void EmptyPlan_EmitsByteIdenticalSource_ToNoPlanEmission()
    {
        // (a) no memoization argument => EmissionContext defaults to Empty.
        var noPlanSource = EmitParserSource(RepresentativeGrammar, plan: null);

        // (b) MemoizationPlan.Empty passed explicitly.
        var emptyPlanSource = EmitParserSource(RepresentativeGrammar, plan: MemoizationPlan.Empty);

        // The core property: the two emissions are byte-for-byte identical.
        Assert.Equal(noPlanSource, emptyPlanSource);
    }

    [Fact]
    public void EmptyPlan_DoesNotEmitMemoField()
    {
        var source = EmitParserSource(RepresentativeGrammar, plan: MemoizationPlan.Empty);

        Assert.DoesNotContain("_memo", source);
    }

    [Fact]
    public void EmptyPlan_DoesNotEmitMemoKeyHelper()
    {
        var source = EmitParserSource(RepresentativeGrammar, plan: MemoizationPlan.Empty);

        Assert.DoesNotContain("MemoKey", source);
    }

    [Fact]
    public void EmptyPlan_DoesNotEmitWrapperBodyMethod()
    {
        var source = EmitParserSource(RepresentativeGrammar, plan: MemoizationPlan.Empty);

        // No probe/store wrapper means no renamed `_ParseR_Body()` method.
        Assert.DoesNotContain("_Body(", source);
    }

    [Fact]
    public void NoPlan_DoesNotEmitMemoizationArtifacts()
    {
        // The default (no-plan) emission must also be free of all memoization
        // artifacts — this is the path existing callers exercise today.
        var source = EmitParserSource(RepresentativeGrammar, plan: null);

        Assert.DoesNotContain("_memo", source);
        Assert.DoesNotContain("MemoKey", source);
        Assert.DoesNotContain("_Body(", source);
    }

    // ---------------------------------------------------------------
    //  Task 4.4 — Memoized wrapper shape
    //  Validates: Requirements 2.7, 2.9, 1.5
    // ---------------------------------------------------------------

    [Fact]
    public void MarkedRule_EmitsWrapperBodyAndMemoizationArtifacts()
    {
        // Force the single rule "Expr" to be memoized. This decouples the emitter
        // test from the analysis heuristic — we directly choose which rule the
        // ParserEmitter wraps. "Expr" is the only marked rule, so it gets ruleId 0.
        var plan = new MemoizationPlan(new[] { "Expr" });

        var source = EmitParserSource(RepresentativeGrammar, plan: plan);

        // The renamed body method (the original parse machinery, under _Body).
        Assert.Contains("_ParseExpr_Body(", source);

        // The thin probe/store wrapper — appears as both the wrapper definition
        // (`_ParseExpr()`) and at call sites; asserting presence is sufficient.
        Assert.Contains("_ParseExpr(", source);

        // The per-parse memo table field.
        Assert.Contains("_memo", source);

        // The (ruleId, pos) key helper.
        Assert.Contains("MemoKey", source);

        // The Init() allocation of a fresh memo table per Parse() call.
        Assert.Contains("_memo = new Dictionary", source);

        // The probe/store shape: the wrapper probes MemoKey(ruleId 0, _pos) and
        // consults the table via TryGetValue. Stable fragments, robust to spacing.
        Assert.Contains("MemoKey(0, _pos)", source);
        Assert.Contains("TryGetValue", source);
    }

    [Fact]
    public void MarkedRuleGrammar_WithEmptyPlan_EmitsNoMemoizationArtifacts()
    {
        // Paired negative: the SAME grammar that CAN be marked, emitted with an
        // empty plan, must contain none of the memoization artifacts. This is the
        // direct positive/negative contrast for task 4.4.
        var source = EmitParserSource(RepresentativeGrammar, plan: MemoizationPlan.Empty);

        Assert.DoesNotContain("_memo", source);
        Assert.DoesNotContain("MemoKey", source);
        Assert.DoesNotContain("_Body(", source);
    }

    // ===============================================================
    //  Task 4.5 — Property 2: Packrat position purity (SR-1)
    //  Validates: Requirements 2.1, 2.2
    //
    //  These tests EMIT a memoized grammar, COMPILE the full generated
    //  parser (runtime + AST nodes + lexer + parser) into an in-memory
    //  assembly via Roslyn, and DRIVE it through reflection. This is
    //  entirely in-process: Roslyn in-memory compilation plus reflection
    //  spawns NO external process (compliant with the workspace
    //  process-spawning policy and the design's process-spawning review).
    //
    //  The grammar below has a single self-recursive rule on a cycle with
    //  a backtracking repetition — the same shape (a re-entered rule
    //  reachable from itself through a '*') that the analysis pass marks
    //  for the real type-argument cycle. The emitter test directly chooses
    //  the memoization plan (marking "SelfRep") so the SR-1 invariant is
    //  exercised independent of the heuristic, mirroring task 4.4.
    // ===============================================================

    // A self-recursive list rule: SelfRep -> SelfRep via the (COMMA SelfRep)* tail.
    // SelfRep is a length-1 SCC with a self-edge whose body backtracks (the '*'),
    // exactly the kind of rule the analysis marks (Requirement 3.6). Invoking
    // SelfRep at a position parses a non-trivial subtree, so reference identity
    // across two invocations at the same position is a strong, observable claim.
    private const string RecursiveListGrammar =
        "token NUM = [0-9]+;\n" +
        "token COMMA = \",\";\n" +
        "rule Start = SelfRep;\n" +
        "rule SelfRep = NUM (COMMA SelfRep)*;";

    private const string MemoizedRuleName = "SelfRep";

    private static readonly object ParserEmitLock = new();

    /// <summary>
    /// Minimal stub for <c>Storm.Testing.NodeOrigin</c> so the emitted
    /// <c>SyntaxNode</c> compiles without referencing the full Example assembly.
    /// </summary>
    private const string NodeOriginStub =
        "namespace Storm.Testing { public abstract record NodeOrigin { } }";

    /// <summary>
    /// Emits the full generated parser for <paramref name="grammar"/> under the
    /// supplied memoization plan and compiles it (runtime types + AST nodes +
    /// lexer + parser + a NodeOrigin stub) into an in-memory assembly via Roslyn.
    /// No external process is spawned.
    /// </summary>
    private static Assembly CompileParser(
        string grammar, MemoizationPlan? plan, string grammarName, string ns)
    {
        var grammarIr = LowerGrammar(grammar, grammarName);
        var context = plan is null
            ? new EmissionContext(ns, grammarName, grammarIr)
            : new EmissionContext(ns, grammarName, grammarIr, plan);

        string runtimeSource, astSource, lexerSource, parserSource;
        lock(ParserEmitLock)
        {
            runtimeSource = AstEmitter.EmitRuntimeTypes();
            astSource = AstEmitter.EmitAstNodes(context);
            lexerSource = LexerEmitter.Emit(context);
            parserSource = ParserEmitter.Emit(context);
        }

        var syntaxTrees = new[]
        {
            CSharpSyntaxTree.ParseText(runtimeSource),
            CSharpSyntaxTree.ParseText(astSource),
            CSharpSyntaxTree.ParseText(lexerSource),
            CSharpSyntaxTree.ParseText(parserSource),
            CSharpSyntaxTree.ParseText(NodeOriginStub),
        };

        var references = new MetadataReference[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(ImmutableArray).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Text.RegularExpressions.Regex).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
            MetadataReference.CreateFromFile(Assembly.Load("System.Collections").Location),
            MetadataReference.CreateFromFile(Assembly.Load("System.Collections.Immutable").Location),
        };

        var compilation = CSharpCompilation.Create(
            "DynamicParser_" + Guid.NewGuid().ToString("N"),
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        Assert.True(result.Success,
            "Parser compilation failed:\n" +
            string.Join("\n", result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.GetMessage())));

        _ = ms.Seek(0, SeekOrigin.Begin);
        return Assembly.Load(ms.ToArray());
    }

    /// <summary>
    /// Lexes <paramref name="input"/> with the compiled grammar's lexer and returns
    /// the resulting <c>TokenStream</c> together with a fresh <c>DiagnosticBag</c>.
    /// </summary>
    private static (object tokenStream, object diagnostics) Lex(
        Assembly asm, string ns, string grammarName, string input)
    {
        var sourceTextType = asm.GetType("Storm.Runtime.SourceText")!;
        var diagnosticBagType = asm.GetType("Storm.Runtime.DiagnosticBag")!;
        var lexerType = asm.GetType($"{ns}.{grammarName}Lexer")!;

        var source = Activator.CreateInstance(sourceTextType, "test.storm", input)!;
        var diagnostics = Activator.CreateInstance(diagnosticBagType)!;

        var lexMethod = lexerType.GetMethod("Lex", BindingFlags.Public | BindingFlags.Static)!;
        var tokenStream = lexMethod.Invoke(null, new[] { source, diagnostics })!;

        return (tokenStream, diagnostics);
    }

    /// <summary>
    /// Invokes the public <c>Parse{ruleName}</c> entrypoint for <paramref name="input"/>
    /// and returns the resulting node (or null).
    /// </summary>
    private static object? ParsePublic(
        Assembly asm, string ns, string grammarName, string ruleName, string input)
    {
        var (tokenStream, diagnostics) = Lex(asm, ns, grammarName, input);
        var parserType = asm.GetType($"{ns}.{grammarName}Parser")!;
        var parseMethod = parserType.GetMethod(
            "Parse" + ruleName, BindingFlags.Public | BindingFlags.Static)!;
        return parseMethod.Invoke(null, new[] { tokenStream, diagnostics });
    }

    /// <summary>
    /// Builds a structural key for a generated AST node: its node-type name, source
    /// span, and the keys of its children in left-to-right order, recursively. This
    /// is exactly the "structurally equal" relation from Requirement 1.1 (same node
    /// type at each position, same parent-child nesting and ordering, same token
    /// span). Token (non-node) fields are not part of <c>Children()</c>; the span
    /// covers the consumed token range, so token positions are reflected in the key.
    /// </summary>
    private static string StructuralKey(object node)
    {
        var type = node.GetType();
        var span = type.GetProperty("Span")!.GetValue(node)!;
        var start = (int)span.GetType().GetProperty("Start")!.GetValue(span)!;
        var length = (int)span.GetType().GetProperty("Length")!.GetValue(span)!;

        var children = (System.Collections.IEnumerable)
            type.GetMethod("Children")!.Invoke(node, null)!;

        var sb = new System.Text.StringBuilder();
        _ = sb.Append(type.Name).Append('[').Append(start).Append(',').Append(length).Append("](");
        foreach(var child in children)
        {
            if(child != null)
            {
                _ = sb.Append(StructuralKey(child)).Append(';');
            }
        }

        _ = sb.Append(')');
        return sb.ToString();
    }

    // FsCheck generator: comma-separated number lists of arity 1..6, each number
    // 0..999. Every sample parses successfully through SelfRep, so SelfRep returns
    // a non-null node at position 0 and reference identity is a meaningful claim.
    private static Gen<string> GenNumberList()
    {
        var numGen = Gen.Choose(0, 999).Select(n => n.ToString());
        return Gen.Choose(1, 6).SelectMany(count =>
            numGen.ArrayOf(count).Select(nums => string.Join(",", nums)));
    }

    private static readonly Lazy<Assembly> MemoizedParser = new(() =>
        CompileParser(RecursiveListGrammar, new MemoizationPlan(new[] { MemoizedRuleName }), "Memo", "Memo.Generated"));

    // Empty plan => the unmemoized body emission (byte-identical to pre-feature).
    private static readonly Lazy<Assembly> UnmemoizedParser = new(() =>
        CompileParser(RecursiveListGrammar, MemoizationPlan.Empty, "Plain", "Plain.Generated"));

    /// <summary>
    /// **Validates: Requirements 2.1, 2.2**
    ///
    /// Property 2 — Packrat determinism (SR-1), clause 1: two invocations of a
    /// memoized rule at the SAME token position over the same stream return a
    /// reference-identical node and leave the cursor (<c>_pos</c>) at the same end
    /// position. The first invocation runs the body (cache miss) and stores
    /// <c>(node, end)</c>; the second invocation at the same position hits the memo
    /// table and returns the SAME node reference with <c>_pos</c> restored to the
    /// same end — without re-running the body.
    ///
    /// Driven in-process via reflection: <c>Init()</c> allocates the memo table,
    /// then the private memoized wrapper <c>_ParseSelfRep()</c> is invoked twice
    /// with <c>_pos</c> reset to the entry position in between (the memo table is
    /// NOT reset, so the second call is a genuine cache hit).
    /// </summary>
    [Property(MaxTest = 50)]
    public Property MemoizedRule_TwoInvocationsAtSamePosition_ReturnReferenceIdenticalNodeAndSameEndPosition()
    {
        const string ns = "Memo.Generated";
        const string grammarName = "Memo";
        var asm = MemoizedParser.Value;

        var parserType = asm.GetType($"{ns}.{grammarName}Parser")!;
        var initMethod = parserType.GetMethod("Init", BindingFlags.NonPublic | BindingFlags.Static)!;
        var memoizedWrapper = parserType.GetMethod(
            "_Parse" + MemoizedRuleName, BindingFlags.NonPublic | BindingFlags.Static)!;
        var posField = parserType.GetField("_pos", BindingFlags.NonPublic | BindingFlags.Static)!;
        var errorField = parserType.GetField("_hasError", BindingFlags.NonPublic | BindingFlags.Static)!;

        // Single compile; drive many inputs (no process spawn anywhere).
        return Prop.ForAll(GenNumberList().ToArbitrary(), input =>
        {
            var (tokenStream, diagnostics) = Lex(asm, ns, grammarName, input);

            // Fresh per-parse state: Init allocates a fresh _memo and sets _pos = 0.
            _ = initMethod.Invoke(null, new[] { tokenStream, diagnostics });
            var entryPos = (int)posField.GetValue(null)!;

            // First invocation at the entry position: cache MISS, runs the body.
            var first = memoizedWrapper.Invoke(null, null);
            var endAfterFirst = (int)posField.GetValue(null)!;

            Assert.True(first != null,
                $"Expected a successful parse (non-null node) for input [{input}].");
            Assert.True(endAfterFirst > entryPos,
                $"Expected the body to advance _pos past the entry position for input [{input}].");

            // Reset ONLY the cursor to the entry position. Leave _memo populated so
            // the second invocation is a genuine cache hit. _hasError must be false
            // after a successful parse, which is the precondition for the probe.
            Assert.False((bool)errorField.GetValue(null)!,
                $"Expected _hasError to be false after a successful parse for input [{input}].");
            posField.SetValue(null, entryPos);

            // Second invocation at the SAME position: cache HIT, must NOT re-run the
            // body and must return the SAME node reference with _pos restored.
            var second = memoizedWrapper.Invoke(null, null);
            var endAfterSecond = (int)posField.GetValue(null)!;

            // SR-1 clause 1: reference-identical node ...
            Assert.True(ReferenceEquals(first, second),
                $"Two invocations at the same position returned different node references for input [{input}].");
            // ... and identical end position.
            Assert.True(endAfterFirst == endAfterSecond,
                $"Two invocations at the same position left _pos at different end positions " +
                $"({endAfterFirst} vs {endAfterSecond}) for input [{input}].");
        });
    }

    /// <summary>
    /// **Validates: Requirements 2.1, 2.2**
    ///
    /// Property 2 — Packrat determinism (SR-1), clause 2: the cached result equals
    /// the unmemoized body's result. The SAME grammar is emitted twice — once with
    /// <c>SelfRep</c> memoized and once with <see cref="MemoizationPlan.Empty"/>
    /// (the unmemoized body emission) — compiled into two in-memory assemblies, and
    /// driven over the same inputs. For every input the two parsers produce a
    /// structurally identical AST (same node type at each position, same parent-child
    /// nesting and ordering, same source-token span), proving memoization does not
    /// alter the parse result. Both parsers are driven in-process; no external
    /// process is spawned.
    /// </summary>
    [Property(MaxTest = 50)]
    public Property MemoizedRule_CachedResult_EqualsUnmemoizedBodyResult()
    {
        const string memoNs = "Memo.Generated";
        const string memoName = "Memo";
        const string plainNs = "Plain.Generated";
        const string plainName = "Plain";

        var memoizedAsm = MemoizedParser.Value;
        var unmemoizedAsm = UnmemoizedParser.Value;

        return Prop.ForAll(GenNumberList().ToArbitrary(), input =>
        {
            var memoizedRoot = ParsePublic(memoizedAsm, memoNs, memoName, "Start", input);
            var unmemoizedRoot = ParsePublic(unmemoizedAsm, plainNs, plainName, "Start", input);

            Assert.True(memoizedRoot != null,
                $"Memoized parser returned null for input [{input}].");
            Assert.True(unmemoizedRoot != null,
                $"Unmemoized parser returned null for input [{input}].");

            // The cached (memoized) result is structurally identical to the
            // unmemoized body's result for the same input.
            Assert.Equal(StructuralKey(unmemoizedRoot!), StructuralKey(memoizedRoot!));
        });
    }

    // ===============================================================
    //  Task 4.6 — Property 3: Cached-failure diagnostic neutrality (SR-2)
    //  Validates: Requirements 2.3, 2.6
    //
    //  These tests EMIT a memoized grammar, COMPILE the full generated
    //  parser into an in-memory assembly via Roslyn, and DRIVE it through
    //  reflection — entirely in-process (no external process is spawned).
    //
    //  The scenario engineered here is a CACHED FAILURE. SelfRep's body
    //  expects a leading NUM token and matches it speculative-safely via the
    //  ok-flag / Peek / Eat path (NOT Require), so when the leading token is
    //  absent the body returns null WITHOUT latching _hasError and WITHOUT
    //  committing a diagnostic (net-zero diagnostic delta). This is exactly
    //  the Speculative_Safe failure the SR-2 contract is about. The memo
    //  wrapper stores such a failure as (null, _entryPos) (Req 2.6) and, on a
    //  subsequent hit, restores _pos to the cached End and returns null
    //  WITHOUT running the body or touching diagnostics (Req 2.2, 2.3).
    // ===============================================================

    // Packed (ruleId, pos) memo key, mirroring the emitted MemoKey helper.
    // SelfRep is the only marked rule, so its ruleId is 0.
    private static long MemoKey(int ruleId, int pos)
    {
        return ((long)ruleId << 32) | (uint)pos;
    }

    // Inputs that make SelfRep FAIL speculative-safely at the entry position
    // (position 0): neither starts with a NUM token, so the body's leading
    // NUM check sets ok=false and returns null without latching _hasError or
    // emitting a diagnostic. "," lexes to a single COMMA token; "" lexes to
    // just EOF. Both are clean, committed-diagnostic-free failures.
    public static IEnumerable<object[]> SpeculativeSafeFailingInputs()
    {
        yield return new object[] { "," };
        yield return new object[] { "" };
    }

    /// <summary>
    /// **Validates: Requirements 2.3, 2.6**
    ///
    /// Property 3 — Diagnostic neutrality (SR-2). On a cache hit for a cached
    /// FAILURE the generated parser MUST (a) return null, (b) restore <c>_pos</c>
    /// to the entry position, (c) make zero net change to the diagnostic bag, and
    /// (d) leave <c>_hasError</c> unchanged from its value at entry — all WITHOUT
    /// re-running the rule body.
    ///
    /// Sequence (driven in-process via reflection):
    ///   1. <c>Init()</c> allocates a fresh <c>_memo</c> and sets <c>_pos = 0</c>.
    ///      Record entryPos, the diagnostic bag Count, and <c>_hasError</c>.
    ///   2. FIRST invocation of <c>_ParseSelfRep()</c> — a cache MISS that runs the
    ///      body. It must fail (null), leave <c>_hasError</c> false (speculative-safe),
    ///      leave <c>_pos</c> back at entryPos, and add nothing net to the bag.
    ///   3. Proof the failure was CACHED: <c>_memo</c> contains the key
    ///      <c>MemoKey(0, entryPos)</c> with stored value <c>(null, entryPos)</c>
    ///      — i.e. End == entry position (Req 2.6). This presence is what makes the
    ///      next call a genuine cache HIT rather than another body run.
    ///   4. SECOND invocation at the SAME position — a cache HIT. It must return
    ///      null, leave <c>_pos == entryPos</c>, leave the bag Count unchanged, and
    ///      leave <c>_hasError</c> unchanged (Req 2.2, 2.3).
    /// </summary>
    [Theory]
    [MemberData(nameof(SpeculativeSafeFailingInputs))]
    public void CachedFailure_CacheHit_IsDiagnosticNeutralAndRestoresEntryPosition(string input)
    {
        const string ns = "Memo.Generated";
        const string grammarName = "Memo";
        var plan = new MemoizationPlan(new[] { MemoizedRuleName });
        var asm = CompileParser(RecursiveListGrammar, plan, grammarName, ns);

        var parserType = asm.GetType($"{ns}.{grammarName}Parser")!;
        var initMethod = parserType.GetMethod("Init", BindingFlags.NonPublic | BindingFlags.Static)!;
        var memoizedWrapper = parserType.GetMethod(
            "_Parse" + MemoizedRuleName, BindingFlags.NonPublic | BindingFlags.Static)!;
        var posField = parserType.GetField("_pos", BindingFlags.NonPublic | BindingFlags.Static)!;
        var errorField = parserType.GetField("_hasError", BindingFlags.NonPublic | BindingFlags.Static)!;
        var memoField = parserType.GetField("_memo", BindingFlags.NonPublic | BindingFlags.Static)!;

        var (tokenStream, diagnostics) = Lex(asm, ns, grammarName, input);
        var diagCountProp = diagnostics.GetType().GetProperty("Count")!;

        // (1) Fresh per-parse state.
        _ = initMethod.Invoke(null, new[] { tokenStream, diagnostics });
        var entryPos = (int)posField.GetValue(null)!;
        var diagCountAtEntry = (int)diagCountProp.GetValue(diagnostics)!;
        var hasErrorAtEntry = (bool)errorField.GetValue(null)!;

        Assert.False(hasErrorAtEntry,
            $"_hasError must be false at entry (precondition for the probe) for input [{input}].");

        // (2) First invocation — cache MISS, runs the body, must fail cleanly.
        var first = memoizedWrapper.Invoke(null, null);
        var posAfterFirst = (int)posField.GetValue(null)!;
        var hasErrorAfterFirst = (bool)errorField.GetValue(null)!;
        var diagCountAfterFirst = (int)diagCountProp.GetValue(diagnostics)!;

        Assert.True(first == null,
            $"Expected SelfRep to FAIL (null) at the entry position for input [{input}].");
        Assert.False(hasErrorAfterFirst,
            $"A speculative-safe failure must NOT latch _hasError for input [{input}].");
        Assert.Equal(entryPos, posAfterFirst);
        Assert.Equal(diagCountAtEntry, diagCountAfterFirst);

        // (3) Proof the failure was cached as (null, entryPos) — this entry is what
        //     turns the second call into a genuine cache HIT (Req 2.6).
        var memo = (System.Collections.IDictionary)memoField.GetValue(null)!;
        var failureKey = MemoKey(0, entryPos);
        Assert.True(memo.Contains(failureKey),
            $"Expected a cached failure entry for MemoKey(0, {entryPos}) after the first call for input [{input}].");

        var valueType = memoField.FieldType.GetGenericArguments()[1]; // (SyntaxNode? Node, int End)
        var storedEntry = memo[failureKey]!;
        var storedNode = valueType.GetField("Item1")!.GetValue(storedEntry);
        var storedEnd = (int)valueType.GetField("Item2")!.GetValue(storedEntry)!;
        Assert.Null(storedNode);              // cached failure => null node
        Assert.Equal(entryPos, storedEnd);    // Req 2.6: End == entry position

        // (4) Second invocation at the SAME position — cache HIT. SR-2 properties:
        //     null result, _pos restored to entry, diagnostic bag net-zero,
        //     _hasError unchanged from entry.
        var second = memoizedWrapper.Invoke(null, null);
        var posAfterSecond = (int)posField.GetValue(null)!;
        var hasErrorAfterSecond = (bool)errorField.GetValue(null)!;
        var diagCountAfterSecond = (int)diagCountProp.GetValue(diagnostics)!;

        Assert.True(second == null,
            $"Cache hit on a cached failure must return null for input [{input}].");
        Assert.Equal(entryPos, posAfterSecond);                 // restores _pos to entry (Req 2.2)
        Assert.Equal(diagCountAtEntry, diagCountAfterSecond);   // zero net diagnostic change (Req 2.3)
        Assert.Equal(hasErrorAtEntry, hasErrorAfterSecond);     // _hasError unchanged (Req 2.3)
    }

    /// <summary>
    /// **Validates: Requirements 2.3, 2.6**
    ///
    /// Stronger "served-from-cache" proof that the cache hit does NOT re-run the
    /// body. After the first (failing) call caches <c>(null, entryPos)</c>, this
    /// test OVERWRITES the cached End with a distinctive out-of-band sentinel
    /// (still a cached failure: Node stays null). On the second invocation:
    ///   - if the wrapper served the result from the cache, it restores
    ///     <c>_pos = sentinel</c> and returns null;
    ///   - if it instead re-ran the body, <c>_pos</c> would land back at entryPos
    ///     (0) and the sentinel would be impossible.
    /// Observing <c>_pos == sentinel</c> after the hit therefore proves the value
    /// came from the cache (Req 2.2's "restore to the cached end position WITHOUT
    /// executing the rule body"), while the diagnostic bag and <c>_hasError</c>
    /// remain untouched (Req 2.3). This distinguishes a genuine HIT from a re-run.
    /// </summary>
    [Fact]
    public void CachedFailure_CacheHit_IsServedFromCache_WithoutReRunningBody()
    {
        const string ns = "Memo.Generated";
        const string grammarName = "Memo";
        const string failingInput = ",";           // SelfRep fails cleanly at pos 0
        const int sentinelEnd = 987654;            // clearly not a real end position

        var plan = new MemoizationPlan(new[] { MemoizedRuleName });
        var asm = CompileParser(RecursiveListGrammar, plan, grammarName, ns);

        var parserType = asm.GetType($"{ns}.{grammarName}Parser")!;
        var initMethod = parserType.GetMethod("Init", BindingFlags.NonPublic | BindingFlags.Static)!;
        var memoizedWrapper = parserType.GetMethod(
            "_Parse" + MemoizedRuleName, BindingFlags.NonPublic | BindingFlags.Static)!;
        var posField = parserType.GetField("_pos", BindingFlags.NonPublic | BindingFlags.Static)!;
        var errorField = parserType.GetField("_hasError", BindingFlags.NonPublic | BindingFlags.Static)!;
        var memoField = parserType.GetField("_memo", BindingFlags.NonPublic | BindingFlags.Static)!;

        var (tokenStream, diagnostics) = Lex(asm, ns, grammarName, failingInput);
        var diagCountProp = diagnostics.GetType().GetProperty("Count")!;

        _ = initMethod.Invoke(null, new[] { tokenStream, diagnostics });
        var entryPos = (int)posField.GetValue(null)!;
        var diagCountAtEntry = (int)diagCountProp.GetValue(diagnostics)!;
        var hasErrorAtEntry = (bool)errorField.GetValue(null)!;

        // First call caches the failure as (null, entryPos).
        var first = memoizedWrapper.Invoke(null, null);
        Assert.True(first == null, "Expected SelfRep to fail at the entry position.");

        var memo = (System.Collections.IDictionary)memoField.GetValue(null)!;
        var failureKey = MemoKey(0, entryPos);
        Assert.True(memo.Contains(failureKey), "Expected the failure to be cached after the first call.");

        // Overwrite the cached End with a sentinel, keeping Node = null (still a
        // cached failure). A body re-run could never produce this position.
        var valueType = memoField.FieldType.GetGenericArguments()[1];
        var tampered = Activator.CreateInstance(valueType, new object?[] { null, sentinelEnd })!;
        memo[failureKey] = tampered;

        // Reset _pos to the entry position so the second call probes the same key.
        posField.SetValue(null, entryPos);

        // Second call — must be served from the (tampered) cache: returns null,
        // restores _pos to the cached sentinel End, and touches nothing else.
        var second = memoizedWrapper.Invoke(null, null);
        var posAfterSecond = (int)posField.GetValue(null)!;
        var hasErrorAfterSecond = (bool)errorField.GetValue(null)!;
        var diagCountAfterSecond = (int)diagCountProp.GetValue(diagnostics)!;

        Assert.True(second == null,
            "Cache hit on a cached failure must return null.");
        Assert.Equal(sentinelEnd, posAfterSecond);             // proves _pos came from the cache, not a re-run
        Assert.Equal(diagCountAtEntry, diagCountAfterSecond);  // diagnostic bag untouched on a hit (Req 2.3)
        Assert.Equal(hasErrorAtEntry, hasErrorAfterSecond);    // _hasError unchanged on a hit (Req 2.3)
    }
}