using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Storm.SourceGenerator.Generator;

namespace Storm.SourceGenerator.Tests;

[Trait("Tier", "4")]
public sealed class NamespacedImportParserTests
{
    private static readonly Dictionary<string, string> Files = new()
    {
        ["root/main.storm"] =
            "grammar Test;\n" +
            "import \"tokens.storm\";\n" +
            "import \"lib.storm\" as L;\n" +
            "import \"lib.storm\" as M;\n" +
            "import \"outer.storm\" as O;\n" +
            "rule Foo = A;\n" +
            "rule L_Foo = B;\n" +
            "rule Start = L.Bar Foo;\n" +
            "rule Twice = first:L.Foo second:M.Foo;\n" +
            "rule Deep = O.Wrap;\n" +
            "rule Picked = L.Pick;\n" +
            "rule Marking = L.Mark;\n" +
            "rule Blocked = L.Block;\n",
        ["root/tokens.storm"] = "token A = \"a\";\ntoken B = \"b\";\ntoken C = \"c\";\nhidden token WS = [ ]+;\n",
        ["root/lib.storm"] =
            "import \"tokens.storm\";\n" +
            "@memo\nrule Foo = B C?;\n" +
            "rule Bar = Foo;\n" +
            "rule Head = B;\n" +
            "rule Wide = Head C;\n" +
            "rule Narrow = Head A;\n" +
            "rule Pick = inline Head: { Wide as WideArm, Narrow as NarrowArm };\n" +
            "trait Marked = mark:B;\n" +
            "rule Mark : Marked = mark:B;\n" +
            "token OPEN = \"{\" => push Inner;\n" +
            "mode Inner {\n    token WORD = [a-z]+;\n    token CLOSE = \"}\" => pop;\n}\n" +
            "rule Block = OPEN WORD CLOSE;\n",
        ["root/outer.storm"] = "import \"inner/inner.storm\" as I;\nrule Wrap = I.Leaf;\n",
        ["root/inner/inner.storm"] = "import \"../tokens.storm\";\nrule Leaf = Foo;\nrule Foo = C;\n",
    };

    private static readonly Lazy<Assembly> Parser = new(Compile);

    [Theory]
    [InlineData("Start", "b c a", "StartNode", 3)]
    [InlineData("Start", "a a", null, 0)]
    [InlineData("L_Foo", "b", "L_FooNode", 1)]
    [InlineData("L_Foo2", "b c", "L_Foo2Node", 2)]
    [InlineData("Twice", "b b c", "TwiceNode", 3)]
    [InlineData("Deep", "c", "DeepNode", 1)]
    [InlineData("Picked", "b a", "PickedNode", 2)]
    [InlineData("Marking", "b", "MarkingNode", 1)]
    [InlineData("Blocked", "{abc}", "BlockedNode", 3)]
    public void AnImportedRuleParsesThroughTheRulesItsOwnFileNames(string rule, string input, string? node, int consumed)
    {
        var source = Activator.CreateInstance(Parser.Value.GetType("Storm.Runtime.SourceText")!, "test.storm", input)!;
        var diagnostics = Activator.CreateInstance(Parser.Value.GetType("Storm.Runtime.DiagnosticBag")!)!;
        var tokens = Parser.Value.GetType("Test.Generated.TestLexer")!.GetMethod("Lex")!.Invoke(null, [source, diagnostics]);
        var parser = Parser.Value.GetType("Test.Generated.TestParser")!;
        var parsed = parser.GetMethod("Parse" + rule)!.Invoke(null, [tokens, diagnostics]);
        var position = (int)parser.GetProperty("LastPosition")!.GetValue(null)!;

        Assert.Equal((node, consumed), (parsed?.GetType().Name, position));
    }

    [Fact]
    public void EachRuleHasOneEntryPoint_TheRootsUnderTheirOwnNames()
    {
        var entries = Parser.Value.GetType("Test.Generated.TestParser")!.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name.StartsWith("Parse", StringComparison.Ordinal))
            .Select(method => method.Name + ":" + method.ReturnType.Name);

        Assert.Equal(
            [
                "ParseFoo:FooNode", "ParseL_Foo:L_FooNode", "ParseStart:StartNode", "ParseTwice:TwiceNode", "ParseDeep:DeepNode",
                "ParsePicked:PickedNode", "ParseMarking:MarkingNode", "ParseBlocked:BlockedNode", "ParseL_Foo2:L_Foo2Node",
                "ParseL_Bar:L_BarNode", "ParseL_Head:L_HeadNode", "ParseL_Wide:L_WideNode", "ParseL_Narrow:L_NarrowNode",
                "ParseL_Pick:L_PickNode", "ParseL_Mark:L_MarkNode", "ParseL_Block:L_BlockNode", "ParseO_Wrap:O_WrapNode",
                "ParseO_I_Leaf:O_I_LeafNode", "ParseO_I_Foo:O_I_FooNode",
            ],
            entries);
        Assert.Contains(Parser.Value.GetType("Test.Generated.L_MarkNode")!.GetInterfaces(), type => type.Name == "IL_MarkedNode");
    }

    private static Assembly Compile()
    {
        var references = new[] { "System.Runtime", "System.Collections", "System.Collections.Immutable", "System.Linq", "System.Text.RegularExpressions" }
            .Select(name => (MetadataReference)MetadataReference.CreateFromFile(Assembly.Load(name).Location))
            .Append(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(ImmutableArray).Assembly.Location));
        var compilation = CSharpCompilation.Create("NamespacedImportParser", [], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        _ = CSharpGeneratorDriver.Create(new StormIncrementalGenerator())
            .AddAdditionalTexts([.. Files.Select(file => (AdditionalText)new GrammarFile(file.Key, file.Value))])
            .RunGeneratorsAndUpdateCompilation(compilation, out var generated, out var generatorDiagnostics);

        Assert.Empty(generatorDiagnostics);

        using var stream = new MemoryStream();
        var emitted = generated.Emit(stream);

        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(10)));

        return Assembly.Load(stream.ToArray());
    }

    private sealed class GrammarFile(string path, string text) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default)
        {
            return SourceText.From(text);
        }
    }
}