using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Tests;

public sealed class OptionalTokenAlternationEmitterTests
{
    [Theory]
    [Trait("Tier", "2")]
    [InlineData(false)]
    [InlineData(true)]
    public void OptionalTokenChoice_ConsumesEitherTokenWhileKeepingTheNullableAstField(bool useLiterals)
    {
        var identifier = new TokenIR("Identifier", new StringLiteralIR("name"), false, 0);
        var discard = new TokenIR("Discard", new StringLiteralIR("_"), false, 1);
        ExpressionIR first = useLiterals ? new StringLiteralIR("name") : new TokenRefIR(identifier);
        ExpressionIR second = useLiterals ? new StringLiteralIR("_") : new TokenRefIR(discard);
        var optional = new OptionalIR(new AlternationIR(new[] { first, second }));
        var alternative = new RuleAlternativeIR(null, new[] { new FieldLabelIR("name", optional) }, optional);
        var rule = new RuleIR("Choice", new[] { alternative });
        var grammar = new GrammarIR(new[] { identifier, discard }, new[] { rule });
        var context = new EmissionContext("Test.Generated", "Test", grammar);

        var parser = ParserEmitter.Emit(context);
        Assert.Matches(
            @"var (_m\d+) = _ok\d+ \? \(Match\(\(int\)TokenKind\.Identifier\) \?\? Match\(\(int\)TokenKind\.Discard\)\) : null;(?s:.*?)_Name = \1;",
            parser);
        Assert.Contains("node.Name = _Name;", parser);

        var ast = AstEmitter.EmitAstNodes(context);
        Assert.Contains("public Token? Name { get; set; }", ast);
    }
}