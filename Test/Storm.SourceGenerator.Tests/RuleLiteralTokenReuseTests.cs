// A string literal in a rule is an implicit reference to the token the lexer produces for its text. Where a declared
// token is spelled exactly so, the lexer produces that one -- a synthetic token for the literal would always lose the
// tie on declaration order and never appear -- so the literal names the declared token and no synthetic one is made.

using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Storm.SourceGenerator.Analyze;
using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Tests;

public sealed class RuleLiteralTokenReuseTests
{
    private const string Tokens = "token IDENT = [a-z]+;\ntoken SEMI = \";\";\n";

    private const string InnerMode = "mode Inner {\n    token INNER_SEMI = \";\";\n}\n";

    private static readonly Lazy<Assembly> Parser = new(Compile);

    private static readonly TokenIR Ident = new("IDENT", new CharClassIR("[a-z]"), isHidden: false, declarationIndex: 0);

    [Theory]
    [Trait("Tier", "1")]
    [InlineData("declared", "SEMI")]
    [InlineData("hidden", "SEMI")]
    [InlineData("through a fragment", "SEMI")]
    [InlineData("twice", "SEMI")]
    [InlineData("in another mode", null)]
    [InlineData("by a pattern", null)]
    [InlineData("synthetically", null)]
    [InlineData("differently", null)]
    public void ALiteralNamesTheFirstDefaultModeTokenSpelledExactlyLikeIt(string spelled, string? named)
    {
        Assert.Equal(named, GrammarIRLowering.DeclaredTokenSpelling(";", TokensSpelling(spelled))?.Name);
    }

    [Theory]
    [Trait("Tier", "3")]
    [InlineData(Tokens, "\";\"", "SEMI", false)]
    [InlineData(Tokens, "\",\"", "__SYNTH_COMMA", true)]
    [InlineData("token IDENT = [a-z]+;\n" + InnerMode, "\";\"", "__SYNTH_SEMI", true)]
    public void ALiteralNamesTheDeclaredTokenSpelledLikeIt_AndIsSynthesizedOnlyWhereNoneIs(string tokens, string literal, string named, bool synthesized)
    {
        var lowering = GrammarIRLowering.Lower(ValidationTestHelpers.BuildGrammar(tokens + "rule Item = name:IDENT " + literal + ";\n"));

        Assert.True(lowering.Succeeded, string.Join("\n", lowering.Diagnostics.Select(d => d.Message)));

        var grammar = lowering.Grammar!;
        var item = (SequenceIR)grammar.Rules.Single(rule => rule.Name == "Item").Alternatives.Single().Expression;
        var token = Assert.IsType<TokenRefIR>(item.Elements[1]).Token;

        Assert.Same(grammar.Tokens.Single(candidate => candidate.Name == named), token);
        Assert.Equal(synthesized ? 1 : 0, grammar.Tokens.Count(candidate => candidate.IsSynthetic));
    }

    [Theory]
    [Trait("Tier", "4")]
    [InlineData("Item", "a;", "ItemNode", 2)]
    [InlineData("Pair", "a,", "PairNode", 2)]
    public void ARuleLiteralParsesWhetherADeclaredTokenOrASyntheticOneCarriesIt(string rule, string input, string node, int consumed)
    {
        var source = Activator.CreateInstance(Parser.Value.GetType("Storm.Runtime.SourceText")!, "test.storm", input)!;
        var diagnostics = Activator.CreateInstance(Parser.Value.GetType("Storm.Runtime.DiagnosticBag")!)!;
        var tokens = Parser.Value.GetType("Test.Generated.TestLexer")!.GetMethod("Lex")!.Invoke(null, [source, diagnostics]);
        var parser = Parser.Value.GetType("Test.Generated.TestParser")!;
        var parsed = parser.GetMethod("Parse" + rule)!.Invoke(null, [tokens, diagnostics]);
        var position = (int)parser.GetProperty("LastPosition")!.GetValue(null)!;

        Assert.Equal((node, consumed), (parsed?.GetType().Name, position));
    }

    private static TokenIR[] TokensSpelling(string spelled)
    {
        return spelled switch
        {
            "declared" => [Ident, Semi(new StringLiteralIR(";"))],
            "hidden" => [Ident, new TokenIR("SEMI", new StringLiteralIR(";"), isHidden: true, declarationIndex: 1)],
            "through a fragment" => [Ident, Semi(new InlineFragmentIR("SEMICOLON", new StringLiteralIR(";")))],
            "twice" => [Ident, new TokenIR("SEMI_AGAIN", new StringLiteralIR(";"), isHidden: false, declarationIndex: 2), Semi(new StringLiteralIR(";"))],
            "in another mode" => [Ident, new TokenIR("SEMI", new StringLiteralIR(";"), isHidden: false, declarationIndex: 1, mode: "Inner")],
            "by a pattern" => [Ident, Semi(new CharClassIR("[;]"))],
            "synthetically" => [Ident, new TokenIR("__SYNTH_SEMI", new StringLiteralIR(";"), isHidden: false, declarationIndex: 1, isSynthetic: true)],
            "differently" => [Ident, Semi(new StringLiteralIR(";;"))],
            _ => throw new ArgumentOutOfRangeException(nameof(spelled), spelled, null),
        };
    }

    private static TokenIR Semi(ExpressionIR pattern)
    {
        return new TokenIR("SEMI", pattern, isHidden: false, declarationIndex: 1);
    }

    private static Assembly Compile()
    {
        var lowering = GrammarIRLowering.Lower(ValidationTestHelpers.BuildGrammar(
            Tokens + "rule Item = name:IDENT \";\";\nrule Pair = name:IDENT \",\";\n"));

        Assert.True(lowering.Succeeded, string.Join("\n", lowering.Diagnostics.Select(d => d.Message)));

        var context = new EmissionContext("Test.Generated", "Test", lowering.Grammar!, MemoizationPlan.Empty, emitObsoleteShims: false);
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
            "RuleLiteralParser",
            sources.Select(source => CSharpSyntaxTree.ParseText(source)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);

        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        return Assembly.Load(stream.ToArray());
    }
}