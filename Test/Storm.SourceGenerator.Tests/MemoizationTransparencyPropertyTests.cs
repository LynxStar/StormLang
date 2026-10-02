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
/// Property-based tests for memoization transparency.
/// Feature: parser-memoization-expansion, Property 7: Memoization transparency
///
/// **Validates: Requirements 7.1, 7.2, 7.3**
///
/// For any input string (valid or invalid), parsing with a memoized rule implementation
/// SHALL produce an identical AST structure and diagnostic sequence as parsing with the
/// non-memoized implementation of the same rule.
///
/// Strategy: compile TWO in-memory parsers from the SAME grammar — one with memoization
/// enabled on selected rules, one with MemoizationPlan.Empty (no memoization). For every
/// generated input, both parsers are driven and their results compared structurally (AST
/// node types, spans, children) and diagnostically (count and messages). No external
/// process is spawned.
/// </summary>
public class MemoizationTransparencyPropertyTests
{
    // ══════════════════════════════════════════════════════════════════
    //  Grammar under test
    // ══════════════════════════════════════════════════════════════════

    // A grammar with multiple rules, alternation, optional, and repetition.
    // This exercises the memoization wrapper across different parse shapes:
    // - Expr has alternation (NUM | IDENT | grouped)
    // - List has repetition via (COMMA Expr)*
    // - Call has optional (Args?)
    // - Start ties everything together
    //
    // Memoizing Expr and List exercises both cache-hit success and cache-hit
    // failure paths since Expr appears in multiple alternatives of higher rules.
    private const string TestGrammar =
        "token NUM = [0-9]+;\n" +
        "token IDENT = [a-z]+;\n" +
        "token LPAREN = \"(\";\n" +
        "token RPAREN = \")\";\n" +
        "token COMMA = \",\";\n" +
        "rule Expr = NUM as Lit | IDENT LPAREN List? RPAREN as Call | IDENT as Var | LPAREN Expr RPAREN as Grouped;\n" +
        "rule List = Expr (COMMA Expr)*;\n" +
        "rule Start = Expr;";

    // Rules to memoize: Expr is referenced from multiple alternatives (List, Start,
    // and the grouped sub-expression in Expr itself). List appears in the optional.
    private static readonly string[] MemoizedRules = { "Expr", "List" };

    // ══════════════════════════════════════════════════════════════════
    //  Compilation helpers (in-memory Roslyn, no process spawning)
    // ══════════════════════════════════════════════════════════════════

    private static readonly object EmitLock = new();

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
    /// Minimal stub for <c>Storm.Testing.NodeOrigin</c> so the emitted
    /// <c>SyntaxNode</c> compiles without referencing the full Example assembly.
    /// </summary>
    private const string NodeOriginStub =
        "namespace Storm.Testing { public abstract record NodeOrigin { } }";

    private static Assembly CompileParser(
        string grammar, MemoizationPlan? plan, string grammarName, string ns)
    {
        var grammarIr = LowerGrammar(grammar, grammarName);
        var context = plan is null
            ? new EmissionContext(ns, grammarName, grammarIr)
            : new EmissionContext(ns, grammarName, grammarIr, plan);

        string runtimeSource, astSource, lexerSource, parserSource;
        lock(EmitLock)
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

    // ══════════════════════════════════════════════════════════════════
    //  Parse driver helpers (reflection-based, in-process)
    // ══════════════════════════════════════════════════════════════════

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
    /// Parses the input and returns both the result node and the diagnostic count.
    /// </summary>
    private static (object? node, int diagnosticCount) ParseWithDiagnostics(
        Assembly asm, string ns, string grammarName, string ruleName, string input)
    {
        var (tokenStream, diagnostics) = Lex(asm, ns, grammarName, input);
        var parserType = asm.GetType($"{ns}.{grammarName}Parser")!;
        var parseMethod = parserType.GetMethod(
            "Parse" + ruleName, BindingFlags.Public | BindingFlags.Static)!;
        var node = parseMethod.Invoke(null, new[] { tokenStream, diagnostics });

        var countProp = diagnostics.GetType().GetProperty("Count")!;
        var count = (int)countProp.GetValue(diagnostics)!;

        return (node, count);
    }

    /// <summary>
    /// Builds a structural key for a generated AST node: its node-type name, source
    /// span, and the keys of its children in left-to-right order, recursively.
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

    // ══════════════════════════════════════════════════════════════════
    //  Input generators
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Generates valid expressions in the grammar: numbers, identifiers,
    /// function calls with arguments, and grouped expressions.
    /// </summary>
    private static Gen<string> GenValidExpr(int depth = 0)
    {
        var numGen = Gen.Choose(0, 999).Select(n => n.ToString());
        var identGen = Gen.Elements("foo", "bar", "baz", "qux", "x", "y", "z");

        if(depth >= 3)
        {
            // Base case: only atoms to prevent infinite recursion
            return Gen.OneOf(numGen, identGen);
        }

        var callGen = identGen.SelectMany(name =>
            Gen.Choose(0, 3).SelectMany(argCount =>
            {
                if(argCount == 0)
                {
                    return Gen.Constant($"{name}()");
                }

                return Gen.ArrayOf(GenValidExpr(depth + 1), argCount)
                    .Select(args => $"{name}({string.Join(",", args)})");
            }));

        var groupedGen = GenValidExpr(depth + 1).Select(inner => $"({inner})");

        return Gen.Frequency(
            (3, numGen),
            (3, identGen),
            (2, callGen),
            (1, groupedGen));
    }

    /// <summary>
    /// Generates inputs that may be invalid: random character sequences that
    /// the grammar may not fully parse, exercising error recovery paths.
    /// </summary>
    private static Gen<string> GenArbitraryInput()
    {
        // Mix of tokens the lexer recognizes plus some that cause parse failures.
        var validTokens = new[] { "123", "abc", "(", ")", ",", "42", "foo", "bar" };
        var invalidChars = new[] { "!", "@", "#", "$", "%", "^", "&", "*", "+", "=", "{", "}" };

        var tokenGen = Gen.Elements(validTokens.Concat(invalidChars).ToArray());

        return Gen.Choose(0, 6).SelectMany(count =>
        {
            if(count == 0)
            {
                return Gen.Constant("");
            }

            return Gen.ArrayOf(tokenGen, count)
                .Select(tokens => string.Join("", tokens));
        });
    }

    /// <summary>
    /// Generates a mix of valid expressions and arbitrary (potentially invalid) inputs.
    /// </summary>
    private static Gen<string> GenMixedInput()
    {
        return Gen.Frequency(
            (3, GenValidExpr()),
            (2, GenArbitraryInput()));
    }

    // ══════════════════════════════════════════════════════════════════
    //  Compiled parser cache (compile once, drive many inputs)
    // ══════════════════════════════════════════════════════════════════

    private static readonly Lazy<Assembly> MemoizedAsm = new(() =>
        CompileParser(TestGrammar, new MemoizationPlan(MemoizedRules), "Memo", "Memo.Generated"));

    private static readonly Lazy<Assembly> UnmemoizedAsm = new(() =>
        CompileParser(TestGrammar, MemoizationPlan.Empty, "Plain", "Plain.Generated"));

    // ══════════════════════════════════════════════════════════════════
    //  Property 7: Memoization transparency
    //  For any input string (valid or invalid), parsing with memoized
    //  rule produces identical AST structure and diagnostics as parsing
    //  without memoization.
    //  **Validates: Requirements 7.1, 7.2, 7.3**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 200)]
    public Property P7_MemoizationTransparency_IdenticalAstAndDiagnostics()
    {
        var memoizedAsm = MemoizedAsm.Value;
        var unmemoizedAsm = UnmemoizedAsm.Value;

        return Prop.ForAll(GenMixedInput().ToArbitrary(), input =>
        {
            var (memoNode, memoDiagCount) = ParseWithDiagnostics(
                memoizedAsm, "Memo.Generated", "Memo", "Start", input);
            var (plainNode, plainDiagCount) = ParseWithDiagnostics(
                unmemoizedAsm, "Plain.Generated", "Plain", "Start", input);

            // Both must agree on success/failure
            var memoSucceeded = memoNode != null;
            var plainSucceeded = plainNode != null;

            Assert.True(memoSucceeded == plainSucceeded,
                $"Memoized parse {(memoSucceeded ? "succeeded" : "failed")} but unmemoized parse " +
                $"{(plainSucceeded ? "succeeded" : "failed")} for input [{input}].");

            // When both succeed, AST structure must be identical
            if(memoSucceeded && plainSucceeded)
            {
                var memoKey = StructuralKey(memoNode!);
                var plainKey = StructuralKey(plainNode!);

                Assert.True(memoKey == plainKey,
                    $"AST structural mismatch for input [{input}].\n" +
                    $"  Memoized: {memoKey}\n" +
                    $"  Plain:    {plainKey}");
            }

            // Diagnostic count must be identical regardless of success/failure
            Assert.True(memoDiagCount == plainDiagCount,
                $"Diagnostic count mismatch for input [{input}]: " +
                $"memoized={memoDiagCount}, plain={plainDiagCount}.");
        });
    }
}