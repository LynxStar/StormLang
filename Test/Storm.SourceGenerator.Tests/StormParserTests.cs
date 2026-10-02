using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;

namespace Storm.SourceGenerator.Tests;

public class StormParserTests
{
    // ---------------------------------------------------------------
    //  Helpers
    // ---------------------------------------------------------------

    private static StormFile Parse(string input, out List<StormDiagnostic> diagnostics)
    {
        diagnostics = new List<StormDiagnostic>();
        var source = new StormSourceText("test.storm", input);
        var tokens = StormLexer.Tokenize(source, diagnostics);
        return StormParser.Parse(tokens, source, diagnostics);
    }

    private static StormFile Parse(string input)
    {
        return Parse(input, out _);
    }

    // ---------------------------------------------------------------
    //  Grammar declaration
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_GrammarDecl_Simple()
    {
        var file = Parse("grammar Example;");
        Assert.NotNull(file.GrammarDeclaration);
        Assert.Equal("Example", file.GrammarDeclaration!.Name);
    }

    [Fact]
    public void Parse_GrammarDecl_BeforeImports()
    {
        var file = Parse("grammar Example;\nimport \"tokens.storm\";", out var diags);
        Assert.Empty(diags);
        Assert.NotNull(file.GrammarDeclaration);
        Assert.Equal("Example", file.GrammarDeclaration!.Name);
        _ = Assert.Single(file.Imports);
    }

    [Fact]
    public void Parse_GrammarDecl_BeforeDeclarations()
    {
        var file = Parse("grammar Test;\ntoken Foo = \"foo\";", out var diags);
        Assert.Empty(diags);
        Assert.NotNull(file.GrammarDeclaration);
        Assert.Equal("Test", file.GrammarDeclaration!.Name);
        _ = Assert.Single(file.Declarations);
    }

    [Fact]
    public void Parse_NoGrammarDecl_IsNull()
    {
        var file = Parse("token Foo = \"foo\";");
        Assert.Null(file.GrammarDeclaration);
    }

    [Fact]
    public void Parse_GrammarDecl_Position()
    {
        var file = Parse("grammar Example;");
        Assert.NotNull(file.GrammarDeclaration);
        Assert.Equal(1, file.GrammarDeclaration!.Line);
        Assert.Equal(1, file.GrammarDeclaration.Column);
    }

    // ---------------------------------------------------------------
    //  Import declarations
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_Import_Simple()
    {
        var file = Parse("import \"lexer.storm\";");
        _ = Assert.Single(file.Imports);
        Assert.Equal("lexer.storm", file.Imports[0].Path);
        Assert.Null(file.Imports[0].Alias);
    }

    [Fact]
    public void Parse_Import_WithAlias()
    {
        var file = Parse("import \"common.storm\" as Common;");
        _ = Assert.Single(file.Imports);
        Assert.Equal("common.storm", file.Imports[0].Path);
        Assert.Equal("Common", file.Imports[0].Alias);
    }

    [Fact]
    public void Parse_MultipleImports()
    {
        var file = Parse("import \"a.storm\";\nimport \"b.storm\" as B;");
        Assert.Equal(2, file.Imports.Count);
        Assert.Equal("a.storm", file.Imports[0].Path);
        Assert.Null(file.Imports[0].Alias);
        Assert.Equal("b.storm", file.Imports[1].Path);
        Assert.Equal("B", file.Imports[1].Alias);
    }

    // ---------------------------------------------------------------
    //  Rule declarations
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_RuleDecl_SimpleReference()
    {
        var file = Parse("rule Expr = Term;");
        _ = Assert.Single(file.Declarations);
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        Assert.Equal("Expr", decl.Name);
        var body = Assert.IsType<StormReferenceExpr>(decl.Body);
        Assert.Equal("Term", body.Name);
    }

    // ---------------------------------------------------------------
    //  Token declarations
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_TokenDecl_StringLiteral()
    {
        var file = Parse("token Plus = \"+\";");
        _ = Assert.Single(file.Declarations);
        var decl = Assert.IsType<StormTokenDecl>(file.Declarations[0]);
        Assert.Equal("Plus", decl.Name);
        var body = Assert.IsType<StormStringLiteralExpr>(decl.Body);
        Assert.Equal("+", body.Value);
    }

    [Fact]
    public void Parse_TokenDecl_CharClass()
    {
        var file = Parse("token Digit = [0-9];");
        _ = Assert.Single(file.Declarations);
        var decl = Assert.IsType<StormTokenDecl>(file.Declarations[0]);
        Assert.Equal("Digit", decl.Name);
        var body = Assert.IsType<StormCharClassExpr>(decl.Body);
        Assert.Equal("[0-9]", body.Text);
    }

    // ---------------------------------------------------------------
    //  Fragment declarations
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_FragmentDecl()
    {
        var file = Parse("fragment Letter = [a-zA-Z];");
        _ = Assert.Single(file.Declarations);
        var decl = Assert.IsType<StormFragmentDecl>(file.Declarations[0]);
        Assert.Equal("Letter", decl.Name);
    }

    // ---------------------------------------------------------------
    //  Hidden token declarations
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_HiddenTokenDecl()
    {
        var file = Parse("hidden token WS = \" \";");
        _ = Assert.Single(file.Declarations);
        var decl = Assert.IsType<StormHiddenTokenDecl>(file.Declarations[0]);
        Assert.Equal("WS", decl.Name);
    }

    // ---------------------------------------------------------------
    //  Expression combinators
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_Sequence()
    {
        var file = Parse("rule Foo = A B C;");
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        var seq = Assert.IsType<StormSequenceExpr>(decl.Body);
        Assert.Equal(3, seq.Elements.Count);
        Assert.All(seq.Elements, e => Assert.IsType<StormReferenceExpr>(e));
    }

    [Fact]
    public void Parse_Alternation()
    {
        var file = Parse("rule Foo = A | B | C;");
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        var alt = Assert.IsType<StormAlternationExpr>(decl.Body);
        Assert.Equal(3, alt.Alternatives.Count);
    }

    [Fact]
    public void Parse_Optional()
    {
        var file = Parse("rule Foo = A?;");
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        var opt = Assert.IsType<StormOptionalExpr>(decl.Body);
        var inner = Assert.IsType<StormReferenceExpr>(opt.Inner);
        Assert.Equal("A", inner.Name);
    }

    [Fact]
    public void Parse_ZeroOrMore()
    {
        var file = Parse("rule Foo = A*;");
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        var star = Assert.IsType<StormZeroOrMoreExpr>(decl.Body);
        var inner = Assert.IsType<StormReferenceExpr>(star.Inner);
        Assert.Equal("A", inner.Name);
    }

    [Fact]
    public void Parse_OneOrMore()
    {
        var file = Parse("rule Foo = A+;");
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        var plus = Assert.IsType<StormOneOrMoreExpr>(decl.Body);
        var inner = Assert.IsType<StormReferenceExpr>(plus.Inner);
        Assert.Equal("A", inner.Name);
    }

    [Fact]
    public void Parse_Group()
    {
        var file = Parse("rule Foo = (A B);");
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        var group = Assert.IsType<StormGroupExpr>(decl.Body);
        var seq = Assert.IsType<StormSequenceExpr>(group.Inner);
        Assert.Equal(2, seq.Elements.Count);
    }

    [Fact]
    public void Parse_GroupedAlternation()
    {
        var file = Parse("rule Foo = (A | B);");
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        var group = Assert.IsType<StormGroupExpr>(decl.Body);
        var alt = Assert.IsType<StormAlternationExpr>(group.Inner);
        Assert.Equal(2, alt.Alternatives.Count);
    }

    [Fact]
    public void Parse_PostfixChaining()
    {
        // A?+ means (A?)+ — optional then one-or-more
        var file = Parse("rule Foo = A?+;");
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        var plus = Assert.IsType<StormOneOrMoreExpr>(decl.Body);
        var opt = Assert.IsType<StormOptionalExpr>(plus.Inner);
        _ = Assert.IsType<StormReferenceExpr>(opt.Inner);
    }

    // ---------------------------------------------------------------
    //  Field labels
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_FieldLabel()
    {
        var file = Parse("rule Foo = lhs:A op:B rhs:C;");
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        var seq = Assert.IsType<StormSequenceExpr>(decl.Body);
        Assert.Equal(3, seq.Elements.Count);

        var lhs = Assert.IsType<StormLabeledExpr>(seq.Elements[0]);
        Assert.Equal("lhs", lhs.Label);
        _ = Assert.IsType<StormReferenceExpr>(lhs.Element);

        var op = Assert.IsType<StormLabeledExpr>(seq.Elements[1]);
        Assert.Equal("op", op.Label);

        var rhs = Assert.IsType<StormLabeledExpr>(seq.Elements[2]);
        Assert.Equal("rhs", rhs.Label);
    }

    [Fact]
    public void Parse_FieldLabel_WithPostfix()
    {
        var file = Parse("rule Foo = items:A*;");
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        var labeled = Assert.IsType<StormLabeledExpr>(decl.Body);
        Assert.Equal("items", labeled.Label);
        _ = Assert.IsType<StormZeroOrMoreExpr>(labeled.Element);
    }

    // ---------------------------------------------------------------
    //  Named alternatives
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_NamedAlternative()
    {
        var file = Parse("rule Expr = A as Literal | B as Binary;");
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        var alt = Assert.IsType<StormAlternationExpr>(decl.Body);
        Assert.Equal(2, alt.Alternatives.Count);

        var first = Assert.IsType<StormNamedAlternative>(alt.Alternatives[0]);
        Assert.Equal("Literal", first.AlternativeName);
        _ = Assert.IsType<StormReferenceExpr>(first.Expression);

        var second = Assert.IsType<StormNamedAlternative>(alt.Alternatives[1]);
        Assert.Equal("Binary", second.AlternativeName);
        _ = Assert.IsType<StormReferenceExpr>(second.Expression);
    }

    [Fact]
    public void Parse_NamedAlternative_WithSequence()
    {
        var file = Parse("rule Expr = lhs:Expr op:Op rhs:Expr as Binary | value:NUMBER as Literal;");
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        var alt = Assert.IsType<StormAlternationExpr>(decl.Body);
        Assert.Equal(2, alt.Alternatives.Count);

        var binary = Assert.IsType<StormNamedAlternative>(alt.Alternatives[0]);
        Assert.Equal("Binary", binary.AlternativeName);
        var seq = Assert.IsType<StormSequenceExpr>(binary.Expression);
        Assert.Equal(3, seq.Elements.Count);

        var literal = Assert.IsType<StormNamedAlternative>(alt.Alternatives[1]);
        Assert.Equal("Literal", literal.AlternativeName);
        _ = Assert.IsType<StormLabeledExpr>(literal.Expression);
    }

    // ---------------------------------------------------------------
    //  Qualified references
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_QualifiedReference()
    {
        var file = Parse("rule Foo = Common.Ident;");
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        var qref = Assert.IsType<StormQualifiedReferenceExpr>(decl.Body);
        Assert.Equal("Common", qref.Qualifier);
        Assert.Equal("Ident", qref.Name);
    }

    // ---------------------------------------------------------------
    //  String literals in expressions
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_StringLiteralInExpression()
    {
        var file = Parse("rule Foo = \"fn\" Ident;");
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        var seq = Assert.IsType<StormSequenceExpr>(decl.Body);
        Assert.Equal(2, seq.Elements.Count);
        var str = Assert.IsType<StormStringLiteralExpr>(seq.Elements[0]);
        Assert.Equal("fn", str.Value);
    }

    // ---------------------------------------------------------------
    //  Character classes in expressions
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_CharClassInExpression()
    {
        var file = Parse("token Ident = [a-zA-Z_] [a-zA-Z0-9_]*;");
        var decl = Assert.IsType<StormTokenDecl>(file.Declarations[0]);
        var seq = Assert.IsType<StormSequenceExpr>(decl.Body);
        Assert.Equal(2, seq.Elements.Count);
        _ = Assert.IsType<StormCharClassExpr>(seq.Elements[0]);
        var star = Assert.IsType<StormZeroOrMoreExpr>(seq.Elements[1]);
        _ = Assert.IsType<StormCharClassExpr>(star.Inner);
    }

    // ---------------------------------------------------------------
    //  Syntax error reporting
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_MissingSemicolon_ReportsError()
    {
        var file = Parse("rule Foo = A", out var diags);
        Assert.Contains(diags, d => d.Id == StormDiagnosticIds.STORM_SYNTAX_ERROR);
    }

    [Fact]
    public void Parse_MissingEquals_ReportsError()
    {
        var file = Parse("rule Foo A;", out var diags);
        Assert.Contains(diags, d => d.Id == StormDiagnosticIds.STORM_SYNTAX_ERROR);
    }

    [Fact]
    public void Parse_UnexpectedToken_ReportsError()
    {
        var file = Parse("rule Foo = ;", out var diags);
        Assert.Contains(diags, d => d.Id == StormDiagnosticIds.STORM_SYNTAX_ERROR);
    }

    [Fact]
    public void Parse_BadImport_ReportsError()
    {
        var file = Parse("import 42;", out var diags);
        Assert.Contains(diags, d => d.Id == StormDiagnosticIds.STORM_SYNTAX_ERROR);
    }

    [Fact]
    public void Parse_RecoveryAfterError_ContinuesParsing()
    {
        // First declaration has an error, second should still parse.
        var file = Parse("rule Foo = ;\nrule Bar = A;", out var diags);
        Assert.Contains(diags, d => d.Id == StormDiagnosticIds.STORM_SYNTAX_ERROR);
        // Bar should still be parsed
        Assert.Contains(file.Declarations, d => d.Name == "Bar");
    }

    // ---------------------------------------------------------------
    //  Complex / integration
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_CompleteGrammarFile()
    {
        var input = @"
import ""tokens.storm"";
import ""common.storm"" as Common;

hidden token WS = "" "";
token Ident = [a-zA-Z_] [a-zA-Z0-9_]*;
fragment Digit = [0-9];
token Number = Digit+;

rule Expr = lhs:Expr op:(""+"" | ""-"") rhs:Expr as Binary
          | value:Number as Literal
          ;
";
        var file = Parse(input, out var diags);
        Assert.Empty(diags);
        Assert.Equal(2, file.Imports.Count);
        Assert.Equal(5, file.Declarations.Count);
        _ = Assert.IsType<StormHiddenTokenDecl>(file.Declarations[0]);
        _ = Assert.IsType<StormTokenDecl>(file.Declarations[1]);
        _ = Assert.IsType<StormFragmentDecl>(file.Declarations[2]);
        _ = Assert.IsType<StormTokenDecl>(file.Declarations[3]);
        _ = Assert.IsType<StormRuleDecl>(file.Declarations[4]);
    }

    [Fact]
    public void Parse_EmptyFile()
    {
        var file = Parse("", out var diags);
        Assert.Empty(diags);
        Assert.Empty(file.Imports);
        Assert.Empty(file.Declarations);
    }

    [Fact]
    public void Parse_CommentsOnly()
    {
        var file = Parse("// just a comment\n// another comment", out var diags);
        Assert.Empty(diags);
        Assert.Empty(file.Imports);
        Assert.Empty(file.Declarations);
    }

    [Fact]
    public void Parse_MixedDeclarationKinds()
    {
        var input = @"
rule A = B;
token C = ""c"";
fragment D = [d];
hidden token E = "" "";
rule F = A | C;
";
        var file = Parse(input, out var diags);
        Assert.Empty(diags);
        Assert.Equal(5, file.Declarations.Count);
        _ = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        _ = Assert.IsType<StormTokenDecl>(file.Declarations[1]);
        _ = Assert.IsType<StormFragmentDecl>(file.Declarations[2]);
        _ = Assert.IsType<StormHiddenTokenDecl>(file.Declarations[3]);
        _ = Assert.IsType<StormRuleDecl>(file.Declarations[4]);
    }

    [Fact]
    public void Parse_EscapedStringLiteral()
    {
        var file = Parse("token Quote = \"\\\"\";", out var diags);
        Assert.Empty(diags);
        var decl = Assert.IsType<StormTokenDecl>(file.Declarations[0]);
        var str = Assert.IsType<StormStringLiteralExpr>(decl.Body);
        Assert.Equal("\"", str.Value);
    }

    // ---------------------------------------------------------------
    //  Regex literals in expressions
    // ---------------------------------------------------------------

    // ---------------------------------------------------------------
    //  @memo annotation parsing
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_MemoAnnotation_ProducesRuleDeclWithIsMemoTrue()
    {
        var file = Parse("@memo rule X = A;", out var diags);
        Assert.Empty(diags);
        _ = Assert.Single(file.Declarations);
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        Assert.Equal("X", decl.Name);
        Assert.True(decl.IsMemo);
        var body = Assert.IsType<StormReferenceExpr>(decl.Body);
        Assert.Equal("A", body.Name);
    }

    [Fact]
    public void Parse_MemoAnnotation_OnToken_ProducesError()
    {
        var file = Parse("@memo token X = \"x\";", out var diags);
        Assert.Contains(diags, d => d.Id == StormDiagnosticIds.STORM_SYNTAX_ERROR);
    }

    [Fact]
    public void Parse_UnrecognizedAnnotation_ProducesError()
    {
        var file = Parse("@unknown rule X = A;", out var diags);
        Assert.Contains(diags, d => d.Id == StormDiagnosticIds.STORM_SYNTAX_ERROR);
    }

    [Fact]
    public void Parse_DuplicateMemoAnnotation_ProducesError()
    {
        var file = Parse("@memo @memo rule X = A;", out var diags);
        Assert.Contains(diags, d => d.Id == StormDiagnosticIds.STORM_SYNTAX_ERROR);
    }

    [Fact]
    public void Parse_MemoAnnotation_ErrorRecovery_ParsesSubsequentDeclarations()
    {
        // @memo on a token should produce an error, but the parser should recover
        // and still parse the subsequent rule declaration.
        var file = Parse("@memo token X = \"x\";\nrule Y = B;", out var diags);
        Assert.Contains(diags, d => d.Id == StormDiagnosticIds.STORM_SYNTAX_ERROR);
        Assert.Contains(file.Declarations, d => d.Name == "Y");
    }

    // ---------------------------------------------------------------
    //  Regex literals in expressions
    // ---------------------------------------------------------------

    // ---------------------------------------------------------------
    //  Trait declarations
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_TraitDecl_OneField()
    {
        var file = Parse("trait Foo = name:Bar;", out var diags);
        Assert.Empty(diags);
        _ = Assert.Single(file.Declarations);
        var decl = Assert.IsType<StormTraitDecl>(file.Declarations[0]);
        Assert.Equal("Foo", decl.Name);
        _ = Assert.Single(decl.Fields);
        Assert.Equal("name", decl.Fields[0].FieldName);
        Assert.Equal("Bar", decl.Fields[0].TypeName);
        Assert.Equal(StormFieldCardinality.One, decl.Fields[0].Cardinality);
        Assert.Empty(decl.ComposedTraits);
    }

    [Fact]
    public void Parse_TraitDecl_MultipleFields_AllCardinalities()
    {
        var file = Parse("trait Foo = a:X b:Y? c:Z* d:W+;", out var diags);
        Assert.Empty(diags);
        _ = Assert.Single(file.Declarations);
        var decl = Assert.IsType<StormTraitDecl>(file.Declarations[0]);
        Assert.Equal("Foo", decl.Name);
        Assert.Equal(4, decl.Fields.Count);

        Assert.Equal("a", decl.Fields[0].FieldName);
        Assert.Equal("X", decl.Fields[0].TypeName);
        Assert.Equal(StormFieldCardinality.One, decl.Fields[0].Cardinality);

        Assert.Equal("b", decl.Fields[1].FieldName);
        Assert.Equal("Y", decl.Fields[1].TypeName);
        Assert.Equal(StormFieldCardinality.Optional, decl.Fields[1].Cardinality);

        Assert.Equal("c", decl.Fields[2].FieldName);
        Assert.Equal("Z", decl.Fields[2].TypeName);
        Assert.Equal(StormFieldCardinality.ZeroOrMore, decl.Fields[2].Cardinality);

        Assert.Equal("d", decl.Fields[3].FieldName);
        Assert.Equal("W", decl.Fields[3].TypeName);
        Assert.Equal(StormFieldCardinality.OneOrMore, decl.Fields[3].Cardinality);
    }

    [Fact]
    public void Parse_TraitDecl_FieldOrderPreserved()
    {
        var file = Parse("trait T = alpha:A beta:B gamma:C;", out var diags);
        Assert.Empty(diags);
        var decl = Assert.IsType<StormTraitDecl>(file.Declarations[0]);
        Assert.Equal(3, decl.Fields.Count);
        Assert.Equal("alpha", decl.Fields[0].FieldName);
        Assert.Equal("beta", decl.Fields[1].FieldName);
        Assert.Equal("gamma", decl.Fields[2].FieldName);
    }

    [Fact]
    public void Parse_TraitDecl_Composition_Single()
    {
        var file = Parse("trait B : A = extra:Foo;", out var diags);
        Assert.Empty(diags);
        _ = Assert.Single(file.Declarations);
        var decl = Assert.IsType<StormTraitDecl>(file.Declarations[0]);
        Assert.Equal("B", decl.Name);
        _ = Assert.Single(decl.ComposedTraits);
        Assert.Equal("A", decl.ComposedTraits[0]);
        _ = Assert.Single(decl.Fields);
        Assert.Equal("extra", decl.Fields[0].FieldName);
        Assert.Equal("Foo", decl.Fields[0].TypeName);
    }

    [Fact]
    public void Parse_TraitDecl_Composition_Multiple()
    {
        var file = Parse("trait C : A, B = extra:Foo;", out var diags);
        Assert.Empty(diags);
        _ = Assert.Single(file.Declarations);
        var decl = Assert.IsType<StormTraitDecl>(file.Declarations[0]);
        Assert.Equal("C", decl.Name);
        Assert.Equal(2, decl.ComposedTraits.Count);
        Assert.Equal("A", decl.ComposedTraits[0]);
        Assert.Equal("B", decl.ComposedTraits[1]);
    }

    [Fact]
    public void Parse_TraitDecl_MissingSemicolon_ReportsError()
    {
        _ = Parse("trait Foo = name:Bar", out var diags);
        Assert.Contains(diags, d => d.Id == StormDiagnosticIds.STORM_SYNTAX_ERROR);
    }

    [Fact]
    public void Parse_TraitDecl_ZeroFields_IsValidMarkerTrait()
    {
        var file = Parse("trait Foo;", out var diags);
        Assert.Empty(diags);
        _ = Assert.Single(file.Declarations);
        var decl = Assert.IsType<StormTraitDecl>(file.Declarations[0]);
        Assert.Equal("Foo", decl.Name);
        Assert.Empty(decl.Fields);
        Assert.Empty(decl.ComposedTraits);
    }

    [Fact]
    public void Parse_RegexLiteral_InTokenBody()
    {
        var file = Parse("token Ident = `[a-zA-Z_]+`;", out var diags);
        Assert.Empty(diags);
        _ = Assert.Single(file.Declarations);
        var decl = Assert.IsType<StormTokenDecl>(file.Declarations[0]);
        var body = Assert.IsType<StormRegexLiteralExpr>(decl.Body);
        Assert.Equal("[a-zA-Z_]+", body.Pattern);
    }

    [Fact]
    public void Parse_RegexLiteral_InFragmentBody()
    {
        var file = Parse("fragment HexDigit = `[0-9a-fA-F]`;", out var diags);
        Assert.Empty(diags);
        _ = Assert.Single(file.Declarations);
        var decl = Assert.IsType<StormFragmentDecl>(file.Declarations[0]);
        var body = Assert.IsType<StormRegexLiteralExpr>(decl.Body);
        Assert.Equal("[0-9a-fA-F]", body.Pattern);
    }

    [Fact]
    public void Parse_RegexLiteral_AsFirstElementInSequence()
    {
        var file = Parse("token Ident = `[a-zA-Z_]` `[a-zA-Z0-9_]*`;", out var diags);
        Assert.Empty(diags);
        _ = Assert.Single(file.Declarations);
        var decl = Assert.IsType<StormTokenDecl>(file.Declarations[0]);
        var seq = Assert.IsType<StormSequenceExpr>(decl.Body);
        Assert.Equal(2, seq.Elements.Count);
        var first = Assert.IsType<StormRegexLiteralExpr>(seq.Elements[0]);
        Assert.Equal("[a-zA-Z_]", first.Pattern);
        var second = Assert.IsType<StormRegexLiteralExpr>(seq.Elements[1]);
        Assert.Equal("[a-zA-Z0-9_]*", second.Pattern);
    }

    // ---------------------------------------------------------------
    //  Rule trait binding parsing
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_RuleTraitBinding_SingleTrait()
    {
        var file = Parse("rule Foo : Bar = name:Name;", out var diags);
        Assert.Empty(diags);
        _ = Assert.Single(file.Declarations);
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        Assert.Equal("Foo", decl.Name);
        Assert.NotNull(decl.TraitBindings);
        Assert.Equal(["Bar"], decl.TraitBindings);
    }

    [Fact]
    public void Parse_RuleTraitBinding_TwoTraits()
    {
        var file = Parse("rule Foo : Bar, Baz = name:Name;", out var diags);
        Assert.Empty(diags);
        _ = Assert.Single(file.Declarations);
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        Assert.Equal("Foo", decl.Name);
        Assert.NotNull(decl.TraitBindings);
        Assert.Equal(["Bar", "Baz"], decl.TraitBindings);
    }

    [Fact]
    public void Parse_RuleTraitBinding_ThreeTraits()
    {
        var file = Parse("rule Foo : A, B, C = name:Name;", out var diags);
        Assert.Empty(diags);
        _ = Assert.Single(file.Declarations);
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        Assert.Equal("Foo", decl.Name);
        Assert.NotNull(decl.TraitBindings);
        Assert.Equal(["A", "B", "C"], decl.TraitBindings);
    }

    [Fact]
    public void Parse_RuleTraitBinding_WithMemoAnnotation()
    {
        var file = Parse("@memo rule Foo : Bar = name:Name;", out var diags);
        Assert.Empty(diags);
        _ = Assert.Single(file.Declarations);
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        Assert.Equal("Foo", decl.Name);
        Assert.True(decl.IsMemo);
        Assert.NotNull(decl.TraitBindings);
        Assert.Equal(["Bar"], decl.TraitBindings);
    }

    [Fact]
    public void Parse_RuleTraitBinding_NoBindingBackwardCompat()
    {
        var file = Parse("rule Foo = name:Name;", out var diags);
        Assert.Empty(diags);
        _ = Assert.Single(file.Declarations);
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        Assert.Equal("Foo", decl.Name);
        Assert.Null(decl.TraitBindings);
    }

    [Fact]
    public void Parse_RuleTraitBinding_ColonButNoIdentifier_ReportsError()
    {
        _ = Parse("rule Foo : = name:Name;", out var diags);
        Assert.Contains(diags, d => d.Id == StormDiagnosticIds.STORM_SYNTAX_ERROR);
    }
}