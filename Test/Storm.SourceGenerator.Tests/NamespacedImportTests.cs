using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;
using Storm.SourceGenerator.Validate;

namespace Storm.SourceGenerator.Tests;

[Trait("Tier", "1")]
public sealed class NamespacedImportTests
{
    [Fact]
    public void AnImportedRuleReadsItsOwnHomonym_WhileTheRootReadsItsOwnAndTheImportedOneByAlias()
    {
        var grammar = Build(
            Root([Import("lib.storm", "L")], Token("A", "a"), Token("B", "b"), Rule("Foo", Ref("A")), Rule("Start", Seq(Qualified("L", "Bar"), Ref("Foo")))),
            Lib("lib.storm", [], Token("C", "c"), Rule("Foo", Ref("C")), Rule("Bar", Ref("Foo"))));

        Assert.Equal(
            ["token A = \"a\";", "token B = \"b\";", "rule Foo = A;", "rule Start = L.Bar Foo;", "token L.C = \"c\";", "rule L.Foo = L.C;", "rule L.Bar = L.Foo;"],
            Printed(grammar));
    }

    [Fact]
    public void ANestedImportIsQualifiedThroughItsImporter_AndHiddenFromTheRoot()
    {
        var grammar = Build(
            Root([Import("a.storm", "A")], Rule("Start", Qualified("A", "Use"))),
            Lib("a.storm", [Import("b.storm", "B")], Rule("Use", Qualified("B", "Leaf"))),
            Lib("b.storm", [], Token("X", "x"), Rule("Leaf", Ref("X"))));

        Assert.Equal(["rule Start = A.Use;", "rule A.Use = A.B.Leaf;", "token A.B.X = \"x\";", "rule A.B.Leaf = A.B.X;"], Printed(grammar));
        Assert.Null(grammar.LookupByName("B.Leaf"));
    }

    [Fact]
    public void TwoAliasesOfOneFileNameTheSameDeclarations()
    {
        var grammar = Build(
            Root([Import("lib.storm", "L"), Import("lib.storm", "M")], Rule("Start", Seq(Qualified("L", "Foo"), Qualified("M", "Foo")))),
            Lib("lib.storm", [], Token("C", "c"), Rule("Foo", Ref("C"))));

        Assert.Equal(["rule Start = L.Foo L.Foo;", "token L.C = \"c\";", "rule L.Foo = L.C;"], Printed(grammar));
        Assert.Same(grammar.LookupByName("L.Foo"), grammar.LookupByName("M.Foo"));
    }

    [Fact]
    public void ADeclarationTheRootAlsoImportsPlainlyKeepsItsPlainName()
    {
        var grammar = Build(
            Root([Import("tokens.storm"), Import("lib.storm", "L")], Rule("Start", Seq(Qualified("L", "Bar"), Qualified("L", "C")))),
            Lib("tokens.storm", [], Token("C", "c")),
            Lib("lib.storm", [Import("tokens.storm")], Rule("Bar", Ref("C"))));

        Assert.Equal(["token C = \"c\";", "rule Start = L.Bar C;", "rule L.Bar = C;"], Printed(grammar));
    }

    [Fact]
    public void ImportedFragmentsModesAndTraitsAreQualifiedWhereverTheImportedFileNamesThem()
    {
        var grammar = Build(
            Root([Import("lib.storm", "L")], Rule("Start", Qualified("L", "Thing"))),
            Lib("lib.storm", [],
                new StormFragmentDecl("DIGIT", new StormCharClassExpr("[0-9]"), 0, 1, 1),
                Token("NUM", new StormOneOrMoreExpr(Ref("DIGIT"))),
                new StormTokenDecl("OPEN", new StormStringLiteralExpr("{"), 0, 1, 1, new StormPushAction("Inner")),
                new StormModeDecl("Inner", [new StormTokenDecl("CLOSE", new StormStringLiteralExpr("}"), 0, 1, 1, new StormPopAction())], 0, 1, 1),
                new StormTraitDecl("Numbered", [new StormTraitField("value", "NUM", StormFieldCardinality.One)], [], 0, 1, 1),
                new StormRuleDecl("Thing", Seq(Ref("OPEN"), new StormLabeledExpr("value", Ref("NUM")), Ref("CLOSE")), 0, 1, 1, TraitBindings: ["Numbered"])));

        Assert.Equal(
            [
                "rule Start = L.Thing;", "fragment L.DIGIT = [0-9];", "token L.NUM = L.DIGIT+;", "token L.OPEN = \"{\" => push L.Inner;",
                "mode L.Inner {\n    token L.CLOSE = \"}\" => pop;\n}", "token L.CLOSE = \"}\" => pop;", "trait L.Numbered = value:L.NUM;",
                "rule L.Thing : L.Numbered = L.OPEN value:L.NUM L.CLOSE;",
            ],
            Printed(grammar));
        Assert.Equal(["default", "L.Inner"], grammar.ModeNames);
        Assert.Empty(NameResolutionPass.Run(grammar).Concat(ModeValidationPass.Run(grammar)).Concat(TraitValidationPass.Run(grammar)));
    }

    [Theory]
    [InlineData("Zed", null, "Undefined reference 'Zed' in declaration 'L.Bar'")]
    [InlineData("Zed", "Q", "Undefined qualified reference 'Q.Zed' in declaration 'L.Bar'")]
    public void AnUndefinedNameInAnImportedFileIsReportedThere_EvenWhereTheRootDeclaresIt(string name, string? qualifier, string message)
    {
        var grammar = Build(
            Root([Import("lib.storm", "L")], Rule("Zed", Ref("A")), Token("A", "a"), Rule("Start", Qualified("L", "Bar"))),
            Lib("lib.storm", [], new StormRuleDecl("Bar", qualifier == null ? Ref(name) : Qualified(qualifier, name), 0, 7, 3)));

        var diagnostic = Assert.Single(grammar.Diagnostics);
        Assert.False(grammar.Succeeded);
        Assert.Equal(
            (StormDiagnosticIds.STORM_UNDEFINED_REFERENCE, StormDiagnosticSeverity.Error, message, "root/lib.storm", 7, 3),
            (diagnostic.Id, diagnostic.Severity, diagnostic.Message, diagnostic.FilePath, diagnostic.Line, diagnostic.Column));
    }

    [Fact]
    public void ADuplicateWithinAnImportedFilesScopeIsReportedWithBothSites()
    {
        var grammar = Build(
            Root([Import("lib.storm", "L")], Rule("Start", Qualified("L", "Dup"))),
            Lib("lib.storm", [Import("common.storm")], new StormRuleDecl("Dup", Ref("C"), 0, 4, 1)),
            Lib("common.storm", [], new StormTokenDecl("C", new StormStringLiteralExpr("c"), 0, 1, 1), new StormTokenDecl("Dup", new StormStringLiteralExpr("d"), 0, 2, 1)));

        var diagnostic = Assert.Single(grammar.Diagnostics);
        Assert.Equal(
            (StormDiagnosticIds.STORM_DUPLICATE_NAME, "Duplicate declaration name 'Dup': first declared in 'root/common.storm' at 2:1, redeclared in 'root/lib.storm' at 4:1"),
            (diagnostic.Id, diagnostic.Message));
    }

    [Theory]
    [InlineData("an imported rule recursing through its own homonym", "Left-recursion detected: L.Foo -> L.Foo")]
    [InlineData("an imported rule reading its own homonym where the root's would close a cycle", null)]
    public void LeftRecursionIsSoughtThroughTheRulesAnImportedFileNames(string shape, string? found)
    {
        var grammar = shape.StartsWith("an imported rule recursing", StringComparison.Ordinal)
            ? Build(
                Root([Import("lib.storm", "L")], Token("A", "a"), Rule("Foo", Ref("A")), Rule("Start", Qualified("L", "Foo"))),
                Lib("lib.storm", [], Token("B", "b"), Rule("Foo", Seq(Ref("Foo"), Ref("B")))))
            : Build(
                Root([Import("lib.storm", "L")], Token("A", "a"), Rule("Foo", Seq(Qualified("L", "Bar"), Ref("A")))),
                Lib("lib.storm", [], Token("B", "b"), Rule("Foo", Ref("B")), Rule("Bar", Seq(Ref("Foo"), Ref("B")))));

        Assert.True(grammar.Succeeded, string.Join("\n", grammar.Diagnostics.Select(d => d.Message)));
        Assert.Equal(found, LeftRecursionPass.Run(grammar).SingleOrDefault()?.Message);
    }

    [Fact]
    public void AQualifiedNameIsEmittedAsAnIdentifierNoRootNameHolds_WhileItsDisplayStaysQualified()
    {
        var lowering = GrammarIRLowering.Lower(Build(
            Root([Import("lib.storm", "L")], Token("B", "b"), Rule("L_Foo", Ref("B")), Rule("Start", Seq(Qualified("L", "Foo"), Ref("L_Foo")))),
            Lib("lib.storm", [], Token("C", "c"), Rule("Foo", Ref("C")))));

        Assert.True(lowering.Succeeded, string.Join("\n", lowering.Diagnostics.Select(d => d.Message)));
        Assert.Equal(
            ["L_Foo L_Foo", "Start Start", "L_Foo2 L.Foo"],
            lowering.Grammar!.Rules.Select(rule => rule.Name + " " + rule.DisplayName));
        Assert.Equal(
            ["B B", "L_C L.C"],
            lowering.Grammar.Tokens.Select(token => token.Name + " " + token.DisplayName));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void AFileTwoNamespacesShareMeansWhatItMeansItself_WhicheverReachesItFirst(bool sharerFirst, bool importsWhatItNames)
    {
        StormImport[] imports = sharerFirst ? [Import("m.storm", "M"), Import("l.storm", "L")] : [Import("l.storm", "L"), Import("m.storm", "M")];
        var grammar = Build(
            Root(imports, Rule("Start", Seq(Qualified("M", "Use"), Qualified("L", "Bar")))),
            Lib("m.storm", [Import("l.storm"), Import("x.storm")], Rule("Use", Ref("Bar"))),
            Lib("l.storm", importsWhatItNames ? [Import("x.storm")] : [], new StormRuleDecl("Bar", Ref("X"), 0, 5, 1)),
            Lib("x.storm", [], Token("X", "x")));

        Assert.Equal(
            importsWhatItNames ? [] : [("Undefined reference 'X' in declaration '" + (sharerFirst ? "M" : "L") + ".Bar'", "root/l.storm")],
            grammar.Diagnostics.Select(diagnostic => (diagnostic.Message, diagnostic.FilePath)));
    }

    [Fact]
    public void AnImportedModeNamedDefaultIsStillTheReservedName()
    {
        var grammar = Build(
            Root([Import("lib.storm", "L")], Rule("Start", Qualified("L", "OPEN"))),
            Lib("lib.storm", [],
                new StormTokenDecl("OPEN", new StormStringLiteralExpr("{"), 0, 1, 1),
                new StormModeDecl("default", [new StormTokenDecl("CLOSE", new StormStringLiteralExpr("}"), 0, 3, 5, new StormPopAction())], 0, 2, 1)));

        Assert.Contains(
            (StormDiagnosticIds.STORM_RESERVED_MODE_NAME, "'L.default' is a reserved mode name and cannot be used as a user-defined mode"),
            ModeValidationPass.Run(grammar).Select(diagnostic => (diagnostic.Id, diagnostic.Message)));
    }

    [Theory]
    [InlineData("a root alternative's node type", "L.Foo", "L_Foo2")]
    [InlineData("a root rule named like a trait's interface", "L.Marked", "L_Marked2")]
    public void AQualifiedNameIsNumberedPastTheTypesARootNameGenerates(string collision, string qualified, string identifier)
    {
        var grammar = collision == "a root alternative's node type"
            ? Build(
                Root([Import("lib.storm", "L")], Token("A", "a"), Token("B", "b"),
                    Rule("Foo", new StormAlternationExpr([new StormNamedAlternative(Ref("A"), "L_"), new StormNamedAlternative(Ref("B"), "Other")]))),
                Lib("lib.storm", [], Token("C", "c"), Rule("Foo", Ref("C"))))
            : Build(
                Root([Import("lib.storm", "L")], Token("A", "a"), Rule("IL_Marked", Ref("A"))),
                Lib("lib.storm", [], Token("C", "c"), new StormTraitDecl("Marked", [new StormTraitField("mark", "C", StormFieldCardinality.One)], [], 0, 1, 1)));

        Assert.True(grammar.Succeeded, string.Join("\n", grammar.Diagnostics.Select(d => d.Message)));
        Assert.Equal(identifier, EmittedNames.Of(grammar)[qualified]);
    }

    private static IReadOnlyList<string> Printed(StormGrammar grammar)
    {
        Assert.True(grammar.Succeeded, string.Join("\n", grammar.Diagnostics.Select(d => d.Message)));
        return grammar.Declarations.Select(declaration => StormPrettyPrinter.Print(new StormFile(null, [], [declaration])).Trim()).ToList();
    }

    private static StormGrammar Build((string Path, StormFile File) root, params (string Path, StormFile File)[] imported)
    {
        var files = new[] { root }.Concat(imported).Select(file => new KeyValuePair<string, StormFile>(file.Path, file.File)).ToList();
        return StormGrammar.Build(new ImportResolutionResult(root.Path, files.ToDictionary(file => file.Key, file => file.Value), files, [], true));
    }

    private static (string, StormFile) Root(StormImport[] imports, params StormDeclaration[] declarations)
    {
        return ("root/main.storm", new StormFile(new StormGrammarDecl("Test", 0, 1, 1), imports, declarations));
    }

    private static (string, StormFile) Lib(string path, StormImport[] imports, params StormDeclaration[] declarations)
    {
        return ("root/" + path, new StormFile(null, imports, declarations));
    }

    private static StormImport Import(string path, string? alias = null)
    {
        return new StormImport(path, alias, 0, 1, 1);
    }

    private static StormTokenDecl Token(string name, string text)
    {
        return Token(name, new StormStringLiteralExpr(text));
    }

    private static StormTokenDecl Token(string name, StormExpression body)
    {
        return new StormTokenDecl(name, body, 0, 1, 1);
    }

    private static StormRuleDecl Rule(string name, StormExpression body)
    {
        return new StormRuleDecl(name, body, 0, 1, 1);
    }

    private static StormExpression Ref(string name)
    {
        return new StormReferenceExpr(name);
    }

    private static StormExpression Qualified(string qualifier, string name)
    {
        return new StormQualifiedReferenceExpr(qualifier, name);
    }

    private static StormExpression Seq(params StormExpression[] elements)
    {
        return new StormSequenceExpr(elements);
    }
}