using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Storm.SourceGenerator.Generator;

namespace Storm.SourceGenerator.Tests;

public class GeneratorPipelineTests
{
    // ---------------------------------------------------------------
    //  Helpers
    // ---------------------------------------------------------------

    private static GeneratorDriverRunResult RunGenerator(string stormGrammar, string fileName = "Test.storm")
    {
        // Create a minimal compilation
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            references: new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
            },
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Create the generator
        var generator = new StormIncrementalGenerator();

        // Create the additional text for the storm file
        var additionalText = new InMemoryAdditionalText(fileName, stormGrammar);

        // Create and run the driver
        var driver = CSharpGeneratorDriver.Create(generator)
            .AddAdditionalTexts(ImmutableArray.Create<AdditionalText>(additionalText));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var _);

        return driver.GetRunResult();
    }

    // ---------------------------------------------------------------
    //  Task 13.1: Generator entry point
    // ---------------------------------------------------------------

    [Fact]
    public void Generator_SimpleGrammar_ProducesExpectedSourceFiles()
    {
        var grammar = "grammar Test;\ntoken Number = [0-9]+;\nhidden token WS = [ \\t]+;\nrule Start = Number;";

        var result = RunGenerator(grammar);

        // Should produce 4 source files: runtime, AST, lexer, parser
        Assert.True(result.GeneratedTrees.Length >= 4,
            $"Expected at least 4 generated files, got {result.GeneratedTrees.Length}. " +
            $"Diagnostics: {string.Join(", ", result.Diagnostics.Select(d => d.GetMessage()))}");
    }

    [Fact]
    public void Generator_SimpleGrammar_ProducesRuntimeTypes()
    {
        var grammar = "grammar Test;\ntoken Number = [0-9]+;\nrule Start = Number;";

        var result = RunGenerator(grammar);

        var runtimeFile = result.GeneratedTrees
            .FirstOrDefault(t => t.FilePath.Contains("Storm.Runtime"));

        Assert.NotNull(runtimeFile);
        var text = runtimeFile!.GetText().ToString();
        Assert.Contains("namespace Storm.Runtime", text);
        Assert.Contains("SyntaxNode", text);
        Assert.Contains("Token", text);
    }

    [Fact]
    public void Generator_SimpleGrammar_ProducesLexer()
    {
        var grammar = "grammar Test;\ntoken Number = [0-9]+;\nrule Start = Number;";

        var result = RunGenerator(grammar);

        var lexerFile = result.GeneratedTrees
            .FirstOrDefault(t => t.FilePath.Contains("Lexer"));

        Assert.NotNull(lexerFile);
        var text = lexerFile!.GetText().ToString();
        Assert.Contains("TokenKind", text);
        Assert.Contains("Lexer", text);
    }

    [Fact]
    public void Generator_SimpleGrammar_ProducesParser()
    {
        var grammar = "grammar Test;\ntoken Number = [0-9]+;\nrule Start = Number;";

        var result = RunGenerator(grammar);

        var parserFile = result.GeneratedTrees
            .FirstOrDefault(t => t.FilePath.Contains("Parser"));

        Assert.NotNull(parserFile);
        var text = parserFile!.GetText().ToString();
        Assert.Contains("Parser", text);
        Assert.Contains("ParseStart", text);
    }

    [Fact]
    public void Generator_SimpleGrammar_ProducesAstNodes()
    {
        var grammar = "grammar Test;\ntoken Number = [0-9]+;\nrule Start = Number;";

        var result = RunGenerator(grammar);

        var astFile = result.GeneratedTrees
            .FirstOrDefault(t => t.FilePath.Contains("Ast"));

        Assert.NotNull(astFile);
        var text = astFile!.GetText().ToString();
        Assert.Contains("StartNode", text);
    }

    [Fact]
    public void Generator_InvalidGrammar_ReportsDiagnostics()
    {
        // This grammar has a syntax error (missing semicolon)
        var grammar = "grammar Test;\ntoken Number = [0-9]+\nrule Start = Number;";

        var result = RunGenerator(grammar);

        // Should have diagnostics
        Assert.True(result.Diagnostics.Length > 0 || result.GeneratedTrees.Length == 0,
            "Expected diagnostics or no generated output for invalid grammar");
    }

    // ---------------------------------------------------------------
    //  Task 13.2: Determinism
    // ---------------------------------------------------------------

    [Fact]
    public void Generator_DeterministicOutput_IdenticalAcrossRuns()
    {
        var grammar = "grammar Test;\ntoken Number = [0-9]+;\ntoken Ident = [a-zA-Z]+;\nhidden token WS = [ \\t\\n]+;\nrule Expr = Number | Ident;\nrule Start = Expr;";

        var result1 = RunGenerator(grammar);
        var result2 = RunGenerator(grammar);

        Assert.Equal(result1.GeneratedTrees.Length, result2.GeneratedTrees.Length);

        for(var i = 0; i < result1.GeneratedTrees.Length; i++)
        {
            var text1 = result1.GeneratedTrees[i].GetText().ToString();
            var text2 = result2.GeneratedTrees[i].GetText().ToString();
            Assert.Equal(text1, text2);
        }
    }

    [Fact]
    public void Generator_EmptyGrammar_ReportsNoRootGrammarDiagnostic()
    {
        var grammar = "";

        var result = RunGenerator(grammar);

        // An empty grammar has no grammar declaration, so it should report STORM_NO_ROOT_GRAMMAR
        Assert.Contains(result.Diagnostics, d => d.Id == "STORM0009");
    }

    // ---------------------------------------------------------------
    //  InMemoryAdditionalText helper
    // ---------------------------------------------------------------

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