using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;
using Storm.SourceGenerator.Resolve;
using Storm.SourceGenerator.Validate;

namespace Storm.SourceGenerator.Tests;

[Trait("Tier", "1")]
public sealed class ModeValidationPassTests
{
    [Theory]
    [InlineData("a token inside the mode it pops", false, new int[0])]
    [InlineData("a default-mode token", false, new[] { 9 })]
    [InlineData("a default-mode token where a mode pushes default", true, new int[0])]
    public void APopIsReportedOnlyOnADefaultModeTokenNoModeReturnsTo(string popping, bool pushesDefault, int[] reportedLines)
    {
        var close = new StormTokenDecl("CLOSE", new StormStringLiteralExpr("}"), 0, 3, 5, new StormPopAction());
        var reopen = new StormTokenDecl("REOPEN", new StormStringLiteralExpr("{"), 0, 4, 5, pushesDefault ? new StormPushAction("default") : null);
        StormDeclaration[] declarations =
        [
            new StormTokenDecl("OPEN", new StormStringLiteralExpr("{"), 0, 1, 1, new StormPushAction("Inner")),
            new StormModeDecl("Inner", [close, reopen], 0, 2, 1),
            .. popping.StartsWith("a default-mode token", StringComparison.Ordinal)
                ? [new StormTokenDecl("STRAY", new StormStringLiteralExpr("!"), 0, 9, 1, new StormPopAction())]
                : Array.Empty<StormDeclaration>(),
        ];

        var diagnostics = ModeValidationPass.Run(Grammar(declarations));

        Assert.All(diagnostics, diagnostic => Assert.Equal((StormDiagnosticIds.STORM_POP_FROM_DEFAULT, StormDiagnosticSeverity.Warning), (diagnostic.Id, diagnostic.Severity)));
        Assert.Equal(reportedLines, diagnostics.Select(diagnostic => diagnostic.Line));
    }

    private static StormGrammar Grammar(StormDeclaration[] declarations)
    {
        var file = new StormFile(new StormGrammarDecl("Test", 0, 1, 1), [], declarations);
        return StormGrammar.Build(new ImportResolutionResult(
            "root/main.storm",
            new Dictionary<string, StormFile> { ["root/main.storm"] = file },
            [new KeyValuePair<string, StormFile>("root/main.storm", file)],
            [],
            true));
    }
}