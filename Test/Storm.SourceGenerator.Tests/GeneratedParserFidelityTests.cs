// The generated parser is compiled with Roslyn and run in-process, because what these tests pin -- a follower token
// consumed, a failed alternative rolled back so that a later one parses, an element matched at any depth, Children()
// walking each real node once, an abandoned round leaving no node behind -- is behaviour of the generated code, which
// reading its text cannot prove. One grammar holds every shape, so the class costs two compilations: one plain, one
// with Item and Captures memoized.

using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Storm.SourceGenerator.Analyze;
using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Tests;

[Trait("Tier", "4")]
public sealed class GeneratedParserFidelityTests
{
    // Outer reads Item twice at the same position. With Item memoized the second read is served from the cache,
    // so the position a failed follower leaves behind is the one the cache replays.
    private const string Grammar =
        "token IDENT = [a-z]+;\n" +
        "token LT = \"<\";\n" +
        "token GT = \">\";\n" +
        "token SEMI = \";\";\n" +
        "token COMMA = \",\";\n" +
        "token LP = \"(\";\n" +
        "token RP = \")\";\n" +
        "hidden token WS = [ ]+;\n" +
        "rule TypeArgs = LT IDENT GT;\n" +
        "@memo\nrule Item = name:IDENT typeArgs:TypeArgs (SEMI | COMMA) as Grouped | name:IDENT as Plain;\n" +
        "rule Outer = item:Item RP as Closed | item:Item as Open;\n" +
        "rule Elem = IDENT;\n" +
        "rule List = first:Elem (COMMA rest:Elem)*;\n" +
        "rule Run = first:Elem (rest:Elem)*;\n" +
        "rule Tuple = LP first:Elem COMMA rest:Elem (COMMA additional:Elem)* RP;\n" +
        "rule Call = name:IDENT TypeArgs?;\n" +
        "rule Held = name:IDENT held:(COMMA (SEMI | LP) inner:Elem RP)?;\n" +
        "rule Nested = first:Elem (LT (COMMA rest:Elem)* GT)?;\n" +
        "rule Pairs = head:IDENT (key:IDENT COMMA? value:IDENT)*;\n" +
        "rule Nest = head:IDENT (key:IDENT (LT value:Elem mark:IDENT GT)? SEMI)*;\n" +
        "rule Parens = first:Elem (COMMA (LP Elem RP))*;\n" +
        "rule Stated = first:Elem (COMMA rest:Elem SEMI)*;\n" +
        "rule Head = first:IDENT LT;\n" +
        "rule Wide = first:IDENT LT value:IDENT GT;\n" +
        "rule Bare = first:IDENT LT SEMI;\n" +
        "rule Pick = inline Head: { wide:Wide as WideArm, bare:Bare as BareArm };\n" +
        "rule Ordered = Elem second:Elem;\n" +
        "rule Interleaved = x:Elem (COMMA a:Elem b:Elem)* SEMI y:Elem;\n" +
        "rule Union = LT first:Elem | LP second:Elem;\n" +
        "rule Alias = SEMI held:(COMMA inner:Elem)? | LP held:(COMMA Elem)?;\n" +
        "rule Shared = (a:Elem b:Elem)* SEMI | LP (c:Elem d:Elem)* GT;\n" +
        "rule Prefix = a:Elem SEMI;\n" +
        "rule Relabel = b:Elem SEMI c:Elem;\n" +
        "rule Other = a:Elem SEMI LP;\n" +
        "rule Projection = inline Prefix: { relabel:Relabel as RelabelArm, other:Other as OtherArm };\n" +
        "rule Direct = head:Prefix tail:Elem;\n" +
        "rule Nominal = inline Prefix: { direct:Direct as DirectArm, other:Other as OtherArm };\n" +
        "@memo\nrule Captures = ordered:Ordered SEMI interleaved:Interleaved GT firstUnion:Union GT secondUnion:Union GT " +
        "firstAlias:Alias GT secondAlias:Alias GT missingAlias:Alias GT firstShared:Shared secondShared:Shared " +
        "projection:Projection GT nominal:Nominal GT other:Projection GT absent:Nominal? GT;\n" +
        "rule CaptureReplay = captures:Captures RP as Closed | captures:Captures as Open;\n";

    private static readonly string[] MemoizedRules = ["Item", "Captures"];

    private static readonly Lazy<Assembly> PlainParser = new(() => Compile(memoized: false));

    private static readonly Lazy<Assembly> MemoizedParser = new(() => Compile(memoized: true));

    [Theory]
    [InlineData("a<b>;", "OpenOuterNode", "GroupedItemNode", 5)]
    [InlineData("a<b>,", "OpenOuterNode", "GroupedItemNode", 5)]
    [InlineData("a<b>;)", "ClosedOuterNode", "GroupedItemNode", 6)]
    [InlineData("a<b>(", "OpenOuterNode", "PlainItemNode", 1)]
    [InlineData("a<b>", "OpenOuterNode", "PlainItemNode", 1)]
    public void AFollowerTokenIsConsumed_AndAMissingOneFallsBackToTheNextAlternative(string input, string outer, string item, int consumed)
    {
        foreach(var (memoized, parser) in Parsers())
        {
            var node = Parse(parser, "Outer", input, out var position);
            var inner = node?.GetType().GetProperty("Item")!.GetValue(node);

            Assert.Equal((memoized, outer, item, consumed), (memoized, node?.GetType().Name, inner?.GetType().Name, position));
        }
    }

    [Theory]
    [InlineData("List", "a,b,c", "a b c")]
    [InlineData("Run", "a b c", "a b c")]
    [InlineData("Tuple", "(a,b)", "a b")]
    [InlineData("Tuple", "(a,b,c,d)", "a b c d")]
    public void ChildrenYieldsEachRepeatedRuleReferenceOnce_InSourceOrder(string rule, string input, string children)
    {
        var node = Parse(PlainParser.Value, rule, input, out _);

        Assert.NotNull(node);
        Assert.Equal(children, string.Join(" ", ChildrenOf(node).Select(child => TextOf(child, input))));
    }

    [Theory]
    [InlineData("Call", "a<b>", 4, "<b>")]
    [InlineData("Held", "a,;b)", 5, "b")]
    [InlineData("Held", "a,;b", 1, "")]
    [InlineData("Nested", "a<,b,c>", 7, "a b c")]
    [InlineData("Pairs", "a b,c d e", 6, "")]
    [InlineData("Parens", "a,(b),(c)", 9, "a b c")]
    [InlineData("Stated", "a,b;,c", 4, "a b")]
    public void AnElementNestedAtAnyDepthIsMatched_AndEveryNodeItReadsIsAChild(string rule, string input, int consumed, string children)
    {
        var node = Parse(PlainParser.Value, rule, input, out var position);

        Assert.NotNull(node);
        Assert.Equal((consumed, children), (position, string.Join(" ", ChildrenOf(node).Select(child => TextOf(child, input)))));
    }

    [Fact]
    public void AGroupNestedInARepeatedGroupIsBuiltInEachRoundThatHoldsIt_AndItsNodeIsAChild()
    {
        const string input = "a b<c m>;d;";
        var node = Parse(PlainParser.Value, "Nest", input, out var position)!;
        var rounds = ((IEnumerable)node.GetType().GetProperty("Group0")!.GetValue(node)!).Cast<object>().Select(round =>
        {
            var nested = round.GetType().GetField("Group0")!.GetValue(round);
            var value = nested?.GetType().GetField("Value")!.GetValue(nested);
            var mark = nested?.GetType().GetField("Mark")!.GetValue(nested);

            return LexemeOf(round.GetType().GetField("Key")!.GetValue(round)) + ":" + (value == null ? "-" : TextOf(value, input) + "/" + LexemeOf(mark));
        });
        _ = node.GetType().GetMethod("SetParentLinks")!.Invoke(node, null);
        var children = ChildrenOf(node).ToList();

        Assert.Equal((9, "b:c/m d:-"), (position, string.Join(" ", rounds)));
        Assert.Equal("c", string.Join(" ", children.Select(child => TextOf(child, input))));
        Assert.All(children, child => Assert.Same(node, child!.GetType().GetProperty("Parent")!.GetValue(child)));
    }

    [Fact]
    public void AnInlinePrefixParsedOnceFillsTheNodeOfWhicheverArmFollows()
    {
        static string armOf(string input)
        {
            var node = Parse(PlainParser.Value, "Pick", input, out _)!;
            var target = node.GetType().GetProperties().Single(property => property.Name is "Wide" or "Bare").GetValue(node)!;
            var value = target.GetType().GetProperty("Value")?.GetValue(target);

            return node.GetType().Name + " " + LexemeOf(target.GetType().GetProperty("First")!.GetValue(target)) + (value == null ? "" : " " + LexemeOf(value));
        }

        Assert.Equal(("WideArmPickNode a b", "BareArmPickNode a"), (armOf("a<b>"), armOf("a<;")));
    }

    [Fact]
    public void ARoundAbandonedAfterItsReferenceParsedLeavesTheLabelledScalarOnTheLastKeptNode()
    {
        const string input = "a,b;,c";
        var node = Parse(PlainParser.Value, "Stated", input, out _)!;

        Assert.Equal("b", TextOf(node.GetType().GetProperty("Rest")!.GetValue(node), input));
    }

    [Fact]
    public void CaptureSlotsAndChildrenAgreeAcrossPropertyKinds_AlternativesAndInlinePrefixes()
    {
        const string input = "a b;c,d e;f><g>(h>;,i>(,j>(>k l;(m n o p>q;r>s;t>u;(>>";
        foreach(var (_, parser) in Parsers())
        {
            var replay = Parse(parser, "CaptureReplay", input, out var position)!;
            var captures = Property(replay, "Captures")!;
            var expected = new Dictionary<string, string>
            {
                ["Ordered"] = "a b",
                ["Interleaved"] = "c d e f",
                ["FirstUnion"] = "g",
                ["SecondUnion"] = "h",
                ["FirstAlias"] = "i",
                ["SecondAlias"] = "j",
                ["MissingAlias"] = "",
                ["FirstShared"] = "k l",
                ["SecondShared"] = "m n o p",
            };
            foreach(var (name, children) in expected)
            {
                var node = Property(captures, name)!;
                Assert.Equal(children, string.Join(" ", ChildrenOf(node).Select(child => TextOf(child, input))));
            }

            Assert.Null(Property(captures, "Absent"));
            var ordered = Property(captures, "Ordered")!;
            Assert.Equal(ChildrenOf(ordered), new[] { Property(ordered, "Elem"), Property(ordered, "Second") });
            Assert.Null(Property(Property(captures, "FirstUnion")!, "Second"));
            Assert.Null(Property(Property(captures, "SecondUnion")!, "First"));
            Assert.Same(Property(Property(captures, "SecondAlias")!, "Held"), ChildrenOf(Property(captures, "SecondAlias")!).Single());

            var relabel = ChildrenOf(Property(captures, "Projection")!).Single()!;
            var direct = ChildrenOf(Property(captures, "Nominal")!).Single()!;
            var other = ChildrenOf(Property(captures, "Other")!).Single()!;
            Assert.Equal("q r", string.Join(" ", ChildrenOf(relabel).Select(child => TextOf(child, input))));
            Assert.Equal("s; t", string.Join(" ", ChildrenOf(direct).Select(child => TextOf(child, input))));
            Assert.Equal("u", string.Join(" ", ChildrenOf(other).Select(child => TextOf(child, input))));
            Assert.Same(Property(relabel, "B"), ChildrenOf(relabel).First());
            Assert.Same(Property(direct, "Head"), ChildrenOf(direct).First());

            _ = replay.GetType().GetMethod("SetParentLinks")!.Invoke(replay, null);
            var origin = new object();
            var reached = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var leaves = new List<string>();
            visit(replay);

            Assert.Equal(input.Count(character => character != ' '), position);
            Assert.Equal("a b c d e f g h i j k l m n o p q r s t u", string.Join(" ", leaves));

            void visit(object node)
            {
                Assert.True(reached.Add(node), "A captured node is reached more than once.");
                node.GetType().GetProperty("Origin")!.SetValue(node, origin);
                if(node.GetType().Name == "ElemNode")
                {
                    leaves.Add(TextOf(node, input));
                }

                foreach(var child in ChildrenOf(node))
                {
                    Assert.NotNull(child);
                    Assert.Same(node, Property(child!, "Parent"));
                    visit(child!);
                    Assert.Same(origin, Property(child!, "Origin"));
                }
            }
        }
    }

    private static object? Property(object node, string name)
    {
        return node.GetType().GetProperty(name)!.GetValue(node);
    }

    private static (bool Memoized, Assembly Parser)[] Parsers()
    {
        return [(false, PlainParser.Value), (true, MemoizedParser.Value)];
    }

    private static object? Parse(Assembly parser, string rule, string input, out int position)
    {
        var source = Activator.CreateInstance(parser.GetType("Storm.Runtime.SourceText")!, "test.storm", input)!;
        var diagnostics = Activator.CreateInstance(parser.GetType("Storm.Runtime.DiagnosticBag")!)!;
        var tokens = parser.GetType("Test.Generated.TestLexer")!.GetMethod("Lex")!.Invoke(null, [source, diagnostics]);
        var parserType = parser.GetType("Test.Generated.TestParser")!;
        var node = parserType.GetMethod("Parse" + rule)!.Invoke(null, [tokens, diagnostics]);
        position = (int)parserType.GetProperty("LastPosition")!.GetValue(null)!;

        return node;
    }

    private static IEnumerable<object?> ChildrenOf(object node)
    {
        return ((IEnumerable)node.GetType().GetMethod("Children")!.Invoke(node, null)!).Cast<object?>();
    }

    private static string LexemeOf(object? token)
    {
        return (string)token!.GetType().GetProperty("Lexeme")!.GetValue(token)!;
    }

    private static string TextOf(object? node, string input)
    {
        if(node == null)
        {
            return "null";
        }

        var span = node.GetType().GetProperty("Span")!.GetValue(node)!;
        var start = (int)span.GetType().GetProperty("Start")!.GetValue(span)!;
        var length = (int)span.GetType().GetProperty("Length")!.GetValue(span)!;

        return input.Substring(start, length);
    }

    private static Assembly Compile(bool memoized)
    {
        var lowering = GrammarIRLowering.Lower(ValidationTestHelpers.BuildGrammar(memoized ? Grammar : Grammar.Replace("@memo\n", "")));

        Assert.True(lowering.Succeeded, string.Join("\n", lowering.Diagnostics.Select(d => d.Message)));

        var plan = memoized ? MemoizationAnalysisPass.Run(lowering.Grammar!) : MemoizationPlan.Empty;

        Assert.All(MemoizedRules, rule => Assert.Equal(memoized, plan.IsMemoized(rule)));

        var context = new EmissionContext("Test.Generated", "Test", lowering.Grammar!, plan, emitObsoleteShims: false);
        string[] sources =
        [
            AstEmitter.EmitRuntimeTypes(),
            AstEmitter.EmitAstNodes(context),
            LexerEmitter.Emit(context),
            ParserEmitter.Emit(context),
            "namespace Storm.Testing { public abstract record NodeOrigin { } }",
        ];

        var references = new[] { "System.Runtime", "System.Collections", "System.Collections.Immutable", "System.Linq", "System.Text.RegularExpressions" }
            .Select(name => (MetadataReference)MetadataReference.CreateFromFile(Assembly.Load(name).Location))
            .Append(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(ImmutableArray).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            "FidelityParser_" + (memoized ? "Memoized" : "Plain"),
            sources.Select(source => CSharpSyntaxTree.ParseText(source)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);

        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        return Assembly.Load(stream.ToArray());
    }
}