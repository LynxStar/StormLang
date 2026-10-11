// A string literal in a rule is an implicit reference to the token the lexer produces for its text. Where a declared
// token accepts that text, the lexer produces the earliest such one -- a synthetic token for the literal would always
// lose the tie on declaration order and never appear -- so the literal names that token, requiring its lexeme when
// the token is a pattern, and no synthetic one is made.

using System.Collections;
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
    [InlineData("declared", "SEMI", null)]
    [InlineData("hidden", "SEMI", null)]
    [InlineData("through a fragment", "SEMI", null)]
    [InlineData("twice", "SEMI", null)]
    [InlineData("in another mode", null, null)]
    [InlineData("by a pattern", "SEMI", ";")]
    [InlineData("by a pattern through a fragment", "SEMI", ";")]
    [InlineData("by a hidden pattern", "SEMI", ";")]
    [InlineData("by a pattern declared before its spelling", "PUNCT", ";")]
    [InlineData("only within longer text", null, null)]
    [InlineData("synthetically", null, null)]
    [InlineData("differently", null, null)]
    public void ALiteralNamesTheEarliestDefaultModeTokenAcceptingItsText_AndRequiresTheTextOfAPattern(string spelled, string? named, string? text)
    {
        var lexed = LiteralTokens.Lexed(";", TokensSpelling(spelled));

        Assert.Equal((named, text), (lexed?.Token.Name, lexed?.Text));
    }

    [Theory]
    [Trait("Tier", "3")]
    [InlineData(Tokens, "\";\"", "SEMI", null, false)]
    [InlineData(Tokens, "\"if\"", "IDENT", "if", false)]
    [InlineData(Tokens, "\",\"", "__SYNTH_COMMA", null, true)]
    [InlineData("token IDENT = [a-z]+;\n" + InnerMode, "\";\"", "__SYNTH_SEMI", null, true)]
    public void ALiteralNamesTheDeclaredTokenLexedForIt_AndIsSynthesizedOnlyWhereNoneIs(string tokens, string literal, string named, string? text, bool synthesized)
    {
        var lowering = GrammarIRLowering.Lower(ValidationTestHelpers.BuildGrammar(tokens + "rule Item = name:IDENT " + literal + ";\n"));

        Assert.True(lowering.Succeeded, string.Join("\n", lowering.Diagnostics.Select(d => d.Message)));

        var grammar = lowering.Grammar!;
        var item = (SequenceIR)grammar.Rules.Single(rule => rule.Name == "Item").Alternatives.Single().Expression;
        var reference = Assert.IsType<TokenRefIR>(item.Elements[1]);

        Assert.Same(grammar.Tokens.Single(candidate => candidate.Name == named), reference.Token);
        Assert.Equal(text, reference.Text);
        Assert.Equal(synthesized ? 1 : 0, grammar.Tokens.Count(candidate => candidate.IsSynthetic));
    }

    [Theory]
    [Trait("Tier", "4")]
    [InlineData("Item", "a;", "ItemNode", 2)]
    [InlineData("Pair", "a,", "PairNode", 2)]
    [InlineData("Word", "if", "WordNode", 1)]
    [InlineData("Word", "iffy", null, 0)]
    [InlineData("Name", "if", "NameNode", 1)]
    [InlineData("Zero", "0", "ZeroNode", 1)]
    [InlineData("Guarded", "if", "GuardedNode", 1)]
    [InlineData("Guarded", "of", null, 0)]
    [InlineData("Choice", "of", "ChoiceNode", 1)]
    [InlineData("Choice", "on", null, 0)]
    public void ARuleLiteralParsesWhereverTheLexerProducesItsText_AndNowhereElse(string rule, string input, string? node, int consumed)
    {
        var parsed = Parse(rule, input, out _, out var position);

        Assert.Equal((node, consumed), (parsed?.GetType().Name, position));
    }

    [Fact]
    [Trait("Tier", "4")]
    public void ALiteralAPatternCarriesHoldsTheTokenTheLexerProduced()
    {
        var parsed = Parse("Word", "if", out var lexed, out _);

        Assert.Equal(lexed[0], parsed!.GetType().GetProperty("Keyword")!.GetValue(parsed));
        Assert.Equal((int)Enum.Parse(Parser.Value.GetType("Test.Generated.TokenKind")!, "IDENT"), lexed[0]!.GetType().GetProperty("Kind")!.GetValue(lexed[0]));
    }

    private static object? Parse(string rule, string input, out IList lexed, out int position)
    {
        var source = Activator.CreateInstance(Parser.Value.GetType("Storm.Runtime.SourceText")!, "test.storm", input)!;
        var diagnostics = Activator.CreateInstance(Parser.Value.GetType("Storm.Runtime.DiagnosticBag")!)!;
        var tokens = Parser.Value.GetType("Test.Generated.TestLexer")!.GetMethod("Lex")!.Invoke(null, [source, diagnostics])!;
        lexed = (IList)tokens.GetType().GetProperty("DefaultChannelTokens")!.GetValue(tokens)!;
        var parser = Parser.Value.GetType("Test.Generated.TestParser")!;
        var parsed = parser.GetMethod("Parse" + rule)!.Invoke(null, [tokens, diagnostics]);
        position = (int)parser.GetProperty("LastPosition")!.GetValue(null)!;
        return parsed;
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
            "by a pattern through a fragment" => [Ident, Semi(new InlineFragmentIR("SEMIS", new OneOrMoreIR(new StringLiteralIR(";"))))],
            "by a hidden pattern" => [Ident, new TokenIR("SEMI", new CharClassIR("[;,]"), isHidden: true, declarationIndex: 1)],
            "by a pattern declared before its spelling" => [Ident, new TokenIR("PUNCT", new CharClassIR("[;,]"), isHidden: false, declarationIndex: 1), new TokenIR("SEMI", new StringLiteralIR(";"), isHidden: false, declarationIndex: 2)],
            "only within longer text" => [Ident, Semi(new SequenceIR([new StringLiteralIR(";"), new CharClassIR("[a-z]")]))],
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
            Tokens + "fragment DIGIT = [0-9];\ntoken NUMBER = DIGIT+;\n" +
            "rule Item = name:IDENT \";\";\nrule Pair = name:IDENT \",\";\nrule Word = keyword:\"if\";\nrule Name = IDENT;\n" +
            "rule Zero = \"0\";\nrule Guarded = &\"if\" IDENT;\nrule Choice = op:(\"if\" | \"of\");\n"));

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