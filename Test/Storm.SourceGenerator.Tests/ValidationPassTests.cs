using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Resolve;
using Storm.SourceGenerator.Validate;

namespace Storm.SourceGenerator.Tests;

// ===================================================================
//  Shared test helpers
// ===================================================================

internal static class ValidationTestHelpers
{
    /// <summary>
    /// Creates a file reader delegate backed by an in-memory dictionary.
    /// </summary>
    public static Func<string, string?> InMemoryFileReader(Dictionary<string, string> files)
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
    /// Builds a StormGrammar from a single in-memory Storm file.
    /// </summary>
    public static StormGrammar BuildGrammar(string source)
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\n" + source,
        };
        var resolver = new StormImportResolver(InMemoryFileReader(files));
        var resolution = resolver.Resolve("root/main.storm");
        return StormGrammar.Build(resolution);
    }

    /// <summary>
    /// Builds a StormGrammar from multiple in-memory Storm files.
    /// </summary>
    public static StormGrammar BuildGrammar(Dictionary<string, string> files, string rootPath)
    {
        var resolver = new StormImportResolver(InMemoryFileReader(files));
        var resolution = resolver.Resolve(rootPath);
        return StormGrammar.Build(resolution);
    }
}

// ===================================================================
//  NameResolutionPass tests
// ===================================================================

public class NameResolutionPassTests
{
    [Fact]
    public void UndefinedUnqualifiedReference_EmitsUndefinedReferenceDiagnostic()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "rule Start = Undefined;");

        var diagnostics = NameResolutionPass.Run(grammar);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_UNDEFINED_REFERENCE &&
            d.Message.Contains("Undefined"));
    }

    [Fact]
    public void UndefinedQualifiedReference_EmitsUndefinedReferenceDiagnostic()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "rule Start = NS.Missing;");

        var diagnostics = NameResolutionPass.Run(grammar);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_UNDEFINED_REFERENCE &&
            d.Message.Contains("NS.Missing"));
    }

    [Fact]
    public void ValidQualifiedReference_NoDiagnostics()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\nimport \"tokens.storm\" as Tok;\nrule Start = Tok.Ident;",
            ["root/tokens.storm"] = "token Ident = [a-z]+;",
        };

        var grammar = ValidationTestHelpers.BuildGrammar(files, "root/main.storm");
        var diagnostics = NameResolutionPass.Run(grammar);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void FragmentReferenceFromRule_EmitsDiagnostic()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "fragment Digit = [0-9];\nrule Start = Digit;");

        var diagnostics = NameResolutionPass.Run(grammar);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_UNDEFINED_REFERENCE &&
            d.Message.Contains("fragment") &&
            d.Message.Contains("lexer-only"));
    }

    [Fact]
    public void FragmentReferenceFromToken_NoDiagnostics()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "fragment Digit = [0-9];\ntoken Number = Digit+;");

        var diagnostics = NameResolutionPass.Run(grammar);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ValidGrammar_NoDiagnostics()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token Ident = [a-z]+;\ntoken Number = [0-9]+;\nrule Expr = Ident | Number;");

        var diagnostics = NameResolutionPass.Run(grammar);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void RuleReferencingAnotherRule_NoDiagnostics()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token Ident = [a-z]+;\nrule Atom = Ident;\nrule Expr = Atom;");

        var diagnostics = NameResolutionPass.Run(grammar);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void MultipleUndefinedReferences_EmitsMultipleDiagnostics()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "rule Start = Missing1 Missing2;");

        var diagnostics = NameResolutionPass.Run(grammar);

        Assert.Equal(2, diagnostics.Count);
        Assert.All(diagnostics, d =>
            Assert.Equal(StormDiagnosticIds.STORM_UNDEFINED_REFERENCE, d.Id));
    }
}

// ===================================================================
//  LeftRecursionPass tests
// ===================================================================

public class LeftRecursionPassTests
{
    [Fact]
    public void DirectLeftRecursion_EmitsDiagnostic()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token Plus = \"+\";\nrule Expr = Expr Plus Expr | \"x\";");

        var diagnostics = LeftRecursionPass.Run(grammar);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_LEFT_RECURSION &&
            d.Message.Contains("Expr"));
    }

    [Fact]
    public void IndirectLeftRecursion_EmitsDiagnostic()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token X = \"x\";\nrule A = B;\nrule B = A | X;");

        var diagnostics = LeftRecursionPass.Run(grammar);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_LEFT_RECURSION);
    }

    [Fact]
    public void RightRecursion_NoDiagnostics()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token X = \"x\";\ntoken Plus = \"+\";\nrule Expr = X Plus Expr | X;");

        var diagnostics = LeftRecursionPass.Run(grammar);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void NoRecursion_NoDiagnostics()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token Ident = [a-z]+;\ntoken Number = [0-9]+;\nrule Expr = Ident | Number;");

        var diagnostics = LeftRecursionPass.Run(grammar);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void TokenOnlyGrammar_NoDiagnostics()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token Ident = [a-z]+;\ntoken Number = [0-9]+;");

        var diagnostics = LeftRecursionPass.Run(grammar);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void LeftRecursionThroughOptional_EmitsDiagnostic()
    {
        // rule A = B? A | "x"; — B is optional, so A can appear at leftmost position
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token X = \"x\";\nrule B = X;\nrule A = B? A | X;");

        var diagnostics = LeftRecursionPass.Run(grammar);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_LEFT_RECURSION &&
            d.Message.Contains("A"));
    }
}

// ===================================================================
//  ReachabilityPass tests
// ===================================================================

public class ReachabilityPassTests
{
    [Fact]
    public void UnreachableAlternative_EmitsWarning()
    {
        // Second alternative "x" is unreachable because first alternative also matches "x"
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token X = \"x\";\nrule Start = X | X;");

        var diagnostics = ReachabilityPass.Run(grammar);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_UNREACHABLE_ALTERNATIVE &&
            d.Severity == StormDiagnosticSeverity.Warning);
    }

    [Fact]
    public void ReachableAlternatives_NoDiagnostics()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token Ident = [a-z]+;\ntoken Number = [0-9]+;\nrule Expr = Ident | Number;");

        var diagnostics = ReachabilityPass.Run(grammar);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void SingleAlternative_NoDiagnostics()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token Ident = [a-z]+;\nrule Start = Ident;");

        var diagnostics = ReachabilityPass.Run(grammar);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ThirdAlternativeShadowed_EmitsWarning()
    {
        // Three alternatives where the third is a subset of the first two combined
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token A = \"a\";\ntoken B = \"b\";\nrule Start = A | B | A;");

        var diagnostics = ReachabilityPass.Run(grammar);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_UNREACHABLE_ALTERNATIVE &&
            d.Message.Contains("3"));
    }

    [Fact]
    public void TokenOnlyGrammar_NoDiagnostics()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token A = \"a\";\ntoken B = \"b\";");

        var diagnostics = ReachabilityPass.Run(grammar);

        Assert.Empty(diagnostics);
    }
}