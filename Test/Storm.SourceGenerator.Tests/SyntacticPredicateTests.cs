// A syntactic predicate, `&E` or `!E`, matches without consuming and yields no AST child. What it buys a PEG
// is an alternative committed to by what FOLLOWS it, which ordered choice alone cannot express: once an
// alternative succeeds locally, a failure later in the enclosing sequence never re-tries the others.

using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Storm.SourceGenerator.Analyze;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.Frontend;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Validate;

namespace Storm.SourceGenerator.Tests;

public class SyntacticPredicateTests
{
    private const string Tokens =
        "token IDENT = [a-z]+;\n" +
        "token LT = \"<\";\n" +
        "token GT = \">\";\n" +
        "token SEMI = \";\";\n" +
        "token COMMA = \",\";\n" +
        "token LP = \"(\";\n" +
        "hidden token WS = [ ]+;\n";

    private const string Followed =
        Tokens +
        "rule Args = LT IDENT GT;\n" +
        "rule Item = name:IDENT args:Args &(SEMI | COMMA) as Stated | name:IDENT !LP as Plain | name:IDENT as Called;\n" +
        "rule Start = Item;\n";

    private static readonly object EmitLock = new();

    [Theory]
    [InlineData("rule R = &A B;", false)]
    [InlineData("rule R = !A B;", true)]
    public void APredicateIsAPrefixOnOneElement(string rule, bool isNegated)
    {
        var sequence = Assert.IsType<StormSequenceExpr>(BodyOf(rule));
        var predicate = Assert.IsType<StormPredicateExpr>(sequence.Elements[0]);

        Assert.Equal(isNegated, predicate.IsNegated);
        Assert.Equal("A", Assert.IsType<StormReferenceExpr>(predicate.Inner).Name);
        Assert.Equal("B", Assert.IsType<StormReferenceExpr>(sequence.Elements[1]).Name);
    }

    [Fact]
    public void APredicateTakesThePostfixOperatorInsideIt()
    {
        var predicate = Assert.IsType<StormPredicateExpr>(BodyOf("rule R = &A*;"));

        _ = Assert.IsType<StormZeroOrMoreExpr>(predicate.Inner);
    }

    [Fact]
    public void APredicateOverAGroupHoldsItsAlternation()
    {
        var sequence = Assert.IsType<StormSequenceExpr>(BodyOf("rule R = A &(B | C);"));
        var predicate = Assert.IsType<StormPredicateExpr>(sequence.Elements[1]);

        _ = Assert.IsType<StormAlternationExpr>(Assert.IsType<StormGroupExpr>(predicate.Inner).Inner);
    }

    [Fact]
    public void ALabelCannotNameAPredicate()
    {
        _ = Parse("rule R = x:&A;", out var diagnostics);

        Assert.NotEmpty(diagnostics);
    }

    [Theory]
    [InlineData("rule R = A &(B | C) as Guarded | A as Bare;")]
    [InlineData("rule R = !A B+;")]
    [InlineData("rule R = (&A B)*;")]
    public void PrintingAPredicateAndParsingItBackIsTheSameRule(string rule)
    {
        var printed = StormPrettyPrinter.Print(Parse("grammar T;\n" + rule, out _));
        var reprinted = StormPrettyPrinter.Print(Parse(printed, out var diagnostics));

        Assert.Empty(diagnostics);
        Assert.Contains(rule.Contains('!') ? "!" : "&", printed);
        Assert.Equal(printed, reprinted);
    }

    [Theory]
    [InlineData("rule A = &A B;\nrule B = IDENT;")]
    [InlineData("rule A = &B A;\nrule B = IDENT;")]
    public void APredicateConsumesNothing_SoRecursionThroughOrPastItIsLeftRecursion(string rules)
    {
        var diagnostics = LeftRecursionPass.Run(ValidationTestHelpers.BuildGrammar(Tokens + rules));

        Assert.Contains(diagnostics, d => d.Id == StormDiagnosticIds.STORM_LEFT_RECURSION);
    }

    [Fact]
    public void ANameInsideAPredicateIsResolved()
    {
        var diagnostics = NameResolutionPass.Run(ValidationTestHelpers.BuildGrammar(Tokens + "rule R = IDENT &Missing;"));

        Assert.Contains(diagnostics, d => d.Id == StormDiagnosticIds.STORM_UNDEFINED_REFERENCE);
    }

    [Theory]
    [InlineData("rule R = IDENT LT GT LP as Call | IDENT LT GT &SEMI as Stated | IDENT as Bare;", false)]
    [InlineData("rule R = IDENT LT GT &SEMI as Stated | IDENT LT GT LP as Call | IDENT as Bare;", false)]
    [InlineData("rule R = IDENT LT GT as Stated | IDENT LT GT LP as Call | IDENT as Bare;", true)]
    public void AGuardedAlternativeShadowsOnlyWhereItsGuardHolds(string rule, bool flagged)
    {
        var diagnostics = ReachabilityPass.Run(ValidationTestHelpers.BuildGrammar(Tokens + rule));

        Assert.Equal(flagged, diagnostics.Exists(d => d.Id == StormDiagnosticIds.STORM_UNREACHABLE_ALTERNATIVE));
    }

    [Fact]
    public void APredicateLowersToItsOwnNodeAndYieldsNoField()
    {
        var item = Lower(Followed, out var diagnostics).Rules.Single(rule => rule.Name == "Item");

        Assert.Empty(diagnostics);
        var expressions = item.Alternatives.SelectMany(alternative => Flatten(alternative.Expression)).ToList();

        Assert.Contains(expressions, expression => expression is PredicateIR { IsNegated: false });
        Assert.Contains(expressions, expression => expression is PredicateIR { IsNegated: true });
        Assert.DoesNotContain("Semi", EmitAst(Followed), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("rule R = IDENT &(x:SEMI);", "Label 'x' inside a predicate")]
    [InlineData("rule R = IDENT &(IDENT*);", "A predicate body must be")]
    [InlineData("rule R = IDENT &(SEMI | COMMA COMMA);", "A predicate body must be")]
    public void APredicateTheParserCouldNotHonourIsRefusedWhereItIsWritten(string rule, string message)
    {
        _ = Lower(Tokens + rule, out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == StormDiagnosticIds.STORM_UNSUPPORTED_CONSTRUCT && d.Message.Contains(message));
    }

    [Fact]
    public void ATokenPredicateIsATestOfWhatIsAheadAndNothingElse()
    {
        var parser = EmitParser(Followed, plan: null);

        Assert.Contains("(Peek(0) == (int)TokenKind.SEMI || Peek(0) == (int)TokenKind.COMMA)", parser);
        Assert.Contains("Peek(0) == (int)TokenKind.LP)", parser);
    }

    [Theory]
    [InlineData("a<b>;", "StatedItemNode", 4)]
    [InlineData("a<b>,", "StatedItemNode", 4)]
    [InlineData("a<b>(", "PlainItemNode", 1)]
    [InlineData("a <b> ;", "StatedItemNode", 4)]
    [InlineData("a<b>", "PlainItemNode", 1)]
    [InlineData("a;", "PlainItemNode", 1)]
    [InlineData("a(", "CalledItemNode", 1)]
    public void AnAlternativeIsChosenByWhatFollowsIt_AndTheFollowerIsLeftUnread(string input, string node, int consumed)
    {
        foreach(var memoized in new[] { false, true })
        {
            var (parsed, position) = ParseItem(Followed, memoized, input);

            Assert.Equal(node, parsed?.GetType().Name);
            Assert.Equal(consumed, position);
        }
    }

    [Theory]
    [InlineData("a;", 1)]
    [InlineData("a a a;", 3)]
    [InlineData("a a (", 1)]
    public void APredicateInsideAnUnlabelledRepetitionGuardsEachRound(string input, int consumed)
    {
        const string grammar = Tokens + "rule Item = head:IDENT (IDENT &(IDENT | SEMI))*;\nrule Start = Item;\n";

        Assert.Equal(consumed, ParseItem(grammar, memoized: false, input).Position);
    }

    [Theory]
    [InlineData("a,b", 2)]
    [InlineData("a,", 1)]
    [InlineData("a", 1)]
    public void APredicateInsideAnUnlabelledOptionalGuardsIt(string input, int consumed)
    {
        const string grammar = Tokens + "rule Item = head:IDENT (COMMA &IDENT)?;\nrule Start = Item;\n";

        Assert.Equal(consumed, ParseItem(grammar, memoized: false, input).Position);
    }

    [Theory]
    [Trait("Tier", "4")]
    [InlineData("a,b", 2)]
    [InlineData("a,", 1)]
    public void APredicateInsideALabelledOptionalGuardsIt(string input, int consumed)
    {
        const string grammar = Tokens + "rule Item = head:IDENT held:(COMMA &IDENT)?;\nrule Start = Item;\n";

        Assert.Equal(consumed, ParseItem(grammar, memoized: false, input).Position);
    }

    [Theory]
    [InlineData("a b", "PairItemNode")]
    [InlineData("a;", "SingleItemNode")]
    public void APredicateLeadingAnAlternativeLooksPastTheElementItGuards(string input, string node)
    {
        const string grammar = Tokens + "rule Item = &(IDENT IDENT) head:IDENT as Pair | head:IDENT as Single;\nrule Start = Item;\n";

        Assert.Equal((node, 1), Described(ParseItem(grammar, memoized: true, input)));
    }

    [Theory]
    [InlineData("a,b<c,d<e;", 9)]
    [InlineData("a,b<c d", 1)]
    public void APredicateInsideAGroupOfLabelledFieldsGuardsEachRound(string input, int consumed)
    {
        const string grammar = Tokens + "rule Item = head:IDENT (COMMA key:IDENT LT value:IDENT &(COMMA | SEMI))*;\nrule Start = Item;\n";

        Assert.Equal(consumed, ParseItem(grammar, memoized: false, input).Position);
    }

    [Theory]
    [InlineData("a!", "BangedItemNode")]
    [InlineData("a;", "PlainItemNode")]
    public void ALiteralInsideAPredicateIsATokenLikeAnyOther(string input, string node)
    {
        const string grammar = Tokens + "rule Item = name:IDENT &\"!\" as Banged | name:IDENT as Plain;\nrule Start = Item;\n";

        Assert.Equal((node, 1), Described(ParseItem(grammar, memoized: false, input)));
    }

    [Fact]
    public void ARecursionThatRunsThroughAPredicateIsACycleForTheMemoizationPlan()
    {
        const string grammar = Tokens +
            "rule Item = IDENT &Nest as Nested | IDENT as Flat;\n" +
            "rule Nest = LT Item GT as Holding | LT GT as Hollow;\nrule Start = Item;\n";

        Assert.True(MemoizationAnalysisPass.Run(Lower(grammar, out _)).IsMemoized("Item"));
    }

    [Fact]
    public void ARuleReferencePredicateRestoresThePositionItTried()
    {
        const string grammar = Tokens +
            "rule Args = LT IDENT GT;\n" +
            "rule Item = name:IDENT &Args as Generic | name:IDENT as Plain;\nrule Start = Item;\n";

        Assert.Equal(("GenericItemNode", 1), Described(ParseItem(grammar, memoized: true, "a<b>")));
        Assert.Equal(("PlainItemNode", 1), Described(ParseItem(grammar, memoized: true, "a<b")));
    }

    private static (string?, int) Described((object? Node, int Position) parsed)
    {
        return (parsed.Node?.GetType().Name, parsed.Position);
    }

    private static StormExpression BodyOf(string rule)
    {
        var file = Parse("grammar T;\n" + rule, out var diagnostics);

        Assert.Empty(diagnostics);

        return Assert.IsType<StormRuleDecl>(Assert.Single(file.Declarations)).Body;
    }

    private static StormFile Parse(string input, out List<StormDiagnostic> diagnostics)
    {
        diagnostics = [];
        var source = new StormSourceText("test.storm", input);

        return StormParser.Parse(StormLexer.Tokenize(source, diagnostics), source, diagnostics);
    }

    private static GrammarIR Lower(string grammar, out List<StormDiagnostic> diagnostics)
    {
        var result = GrammarIRLowering.Lower(ValidationTestHelpers.BuildGrammar(grammar));
        diagnostics = [.. result.Diagnostics];

        return result.Grammar!;
    }

    private static IEnumerable<ExpressionIR> Flatten(ExpressionIR expression)
    {
        return InnerOf(expression).SelectMany(Flatten).Prepend(expression);
    }

    private static IReadOnlyList<ExpressionIR> InnerOf(ExpressionIR expression)
    {
        return expression switch
        {
            SequenceIR sequence => sequence.Elements,
            AlternationIR alternation => alternation.Alternatives,
            OptionalIR optional => [optional.Inner],
            ZeroOrMoreIR zeroOrMore => [zeroOrMore.Inner],
            OneOrMoreIR oneOrMore => [oneOrMore.Inner],
            _ => [],
        };
    }

    private static EmissionContext ContextOf(string grammar, bool memoized)
    {
        var lowered = Lower(memoized ? grammar.Replace("rule Item =", "@memo\nrule Item =") : grammar, out var diagnostics);

        Assert.Empty(diagnostics);

        return memoized
            ? new EmissionContext("Test.Generated", "Test", lowered, MemoizationAnalysisPass.Run(lowered))
            : new EmissionContext("Test.Generated", "Test", lowered);
    }

    private static string EmitParser(string grammar, MemoizationPlan? plan)
    {
        _ = plan;

        return ParserEmitter.Emit(ContextOf(grammar, memoized: false));
    }

    private static string EmitAst(string grammar)
    {
        return AstEmitter.EmitAstNodes(ContextOf(grammar, memoized: false));
    }

    private static (object? Node, int Position) ParseItem(string grammar, bool memoized, string input)
    {
        var context = ContextOf(grammar, memoized);
        string[] sources;

        lock(EmitLock)
        {
            sources =
            [
                AstEmitter.EmitRuntimeTypes(),
                AstEmitter.EmitAstNodes(context),
                LexerEmitter.Emit(context),
                ParserEmitter.Emit(context),
                "namespace Storm.Testing { public abstract record NodeOrigin { } }",
            ];
        }

        var references = new[] { "System.Runtime", "System.Collections", "System.Collections.Immutable", "System.Linq", "System.Text.RegularExpressions" }
            .Select(name => (MetadataReference)MetadataReference.CreateFromFile(Assembly.Load(name).Location))
            .Append(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(ImmutableArray).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            "PredicateParser_" + Guid.NewGuid().ToString("N"),
            sources.Select(source => CSharpSyntaxTree.ParseText(source)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);

        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        var assembly = Assembly.Load(stream.ToArray());
        var source = Activator.CreateInstance(assembly.GetType("Storm.Runtime.SourceText")!, "test.storm", input)!;
        var bag = Activator.CreateInstance(assembly.GetType("Storm.Runtime.DiagnosticBag")!)!;
        var tokens = assembly.GetType("Test.Generated.TestLexer")!.GetMethod("Lex", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [source, bag])!;
        var parser = assembly.GetType("Test.Generated.TestParser")!;
        var node = parser.GetMethod("ParseItem", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [tokens, bag]);
        var position = (int)parser.GetProperty("LastPosition", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

        return (node, position);
    }
}