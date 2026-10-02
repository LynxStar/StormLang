using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Tests;

public class GrammarIRLoweringTests
{
    // ---------------------------------------------------------------
    //  Helpers
    // ---------------------------------------------------------------

    private static Func<string, string?> InMemoryFileReader(Dictionary<string, string> files)
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

    private static GrammarIRLowering.LoweringResult LowerGrammar(Dictionary<string, string> files, string rootPath)
    {
        var resolver = new StormImportResolver(InMemoryFileReader(files));
        var resolution = resolver.Resolve(rootPath);
        var grammar = StormGrammar.Build(resolution);
        return GrammarIRLowering.Lower(grammar);
    }

    // ---------------------------------------------------------------
    //  Fragment inlining
    // ---------------------------------------------------------------

    [Fact]
    public void Lower_TokenReferencingFragment_InlinesFragmentExpression()
    {
        // Storm uses double-quoted string literals
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\nfragment Digit = [0-9];\ntoken Number = Digit+;\nrule Start = Number;"
        };

        var result = LowerGrammar(files, "root/main.storm");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Grammar);

        // Number token should have its fragment inlined
        var numberToken = result.Grammar!.Tokens.FirstOrDefault(t => t.Name == "Number");
        Assert.NotNull(numberToken);

        // The expression should be OneOrMore(InlineFragment("Digit", CharClass("[0-9]")))
        var oneOrMore = Assert.IsType<OneOrMoreIR>(numberToken!.Expression);
        var inlineFrag = Assert.IsType<InlineFragmentIR>(oneOrMore.Inner);
        Assert.Equal("Digit", inlineFrag.FragmentName);
        _ = Assert.IsType<CharClassIR>(inlineFrag.Body);
        var charClass = (CharClassIR)inlineFrag.Body;
        Assert.Equal("[0-9]", charClass.Text);
    }

    [Fact]
    public void Lower_NestedFragmentInlining_InlinesDeeply()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\nfragment Letter = [a-zA-Z];\nfragment LetterOrDigit = Letter | [0-9];\ntoken Identifier = Letter LetterOrDigit*;\nrule Start = Identifier;"
        };

        var result = LowerGrammar(files, "root/main.storm");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Grammar);

        var identToken = result.Grammar!.Tokens.FirstOrDefault(t => t.Name == "Identifier");
        Assert.NotNull(identToken);

        // Expression should be Sequence(InlineFragment("Letter", ...), ZeroOrMore(InlineFragment("LetterOrDigit", ...)))
        var seq = Assert.IsType<SequenceIR>(identToken!.Expression);
        Assert.Equal(2, seq.Elements.Count);

        // First element: inlined Letter fragment
        var letterFrag = Assert.IsType<InlineFragmentIR>(seq.Elements[0]);
        Assert.Equal("Letter", letterFrag.FragmentName);
        _ = Assert.IsType<CharClassIR>(letterFrag.Body);

        // Second element: ZeroOrMore(inlined LetterOrDigit fragment)
        var zeroOrMore = Assert.IsType<ZeroOrMoreIR>(seq.Elements[1]);
        var lodFrag = Assert.IsType<InlineFragmentIR>(zeroOrMore.Inner);
        Assert.Equal("LetterOrDigit", lodFrag.FragmentName);

        // LetterOrDigit body should be Alternation(InlineFragment("Letter", ...), CharClass("[0-9]"))
        var altBody = Assert.IsType<AlternationIR>(lodFrag.Body);
        Assert.Equal(2, altBody.Alternatives.Count);
        _ = Assert.IsType<InlineFragmentIR>(altBody.Alternatives[0]);
        _ = Assert.IsType<CharClassIR>(altBody.Alternatives[1]);
    }

    [Fact]
    public void Lower_FragmentsNotInTokenList()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\nfragment Digit = [0-9];\ntoken Number = Digit+;\nrule Start = Number;"
        };

        var result = LowerGrammar(files, "root/main.storm");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Grammar);

        // Fragments should NOT appear in the token list
        Assert.DoesNotContain(result.Grammar!.Tokens, t => t.Name == "Digit");
        // Only Number should be in the token list
        Assert.Contains(result.Grammar.Tokens, t => t.Name == "Number");
    }

    // ---------------------------------------------------------------
    //  Synthetic token creation for anonymous string literals
    // ---------------------------------------------------------------

    [Fact]
    public void Lower_AnonymousStringLiteralInRule_CreatesSyntheticToken()
    {
        // Use escaped double quotes for the anonymous string literal "+"
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\ntoken Number = [0-9]+;\nrule Expr = Number \"+\" Number;"
        };

        var result = LowerGrammar(files, "root/main.storm");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Grammar);

        // A synthetic token for "+" should have been created
        var synthToken = result.Grammar!.Tokens.FirstOrDefault(t => t.IsSynthetic);
        Assert.NotNull(synthToken);
        _ = Assert.IsType<StringLiteralIR>(synthToken!.Expression);
        Assert.Equal("+", ((StringLiteralIR)synthToken.Expression).Value);
    }

    [Fact]
    public void Lower_DuplicateAnonymousLiterals_CreatesSingleSyntheticToken()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\ntoken Number = [0-9]+;\nrule Expr = Number \"+\" Number;\nrule Stmt = Number \"+\" Number;"
        };

        var result = LowerGrammar(files, "root/main.storm");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Grammar);

        // Only one synthetic token for "+" should exist
        var synthPlusTokens = result.Grammar!.Tokens.Where(t => t.IsSynthetic).ToList();
        _ = Assert.Single(synthPlusTokens);
    }

    [Fact]
    public void Lower_MultipleDistinctAnonymousLiterals_CreatesMultipleSyntheticTokens()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\ntoken Number = [0-9]+;\nrule Expr = Number \"+\" Number | Number \"-\" Number;"
        };

        var result = LowerGrammar(files, "root/main.storm");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Grammar);

        var synthTokens = result.Grammar!.Tokens.Where(t => t.IsSynthetic).ToList();
        Assert.Equal(2, synthTokens.Count);
    }

    [Fact]
    public void Lower_AnonymousStringLiteralInRule_ResolvedToTokenRef()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\ntoken Number = [0-9]+;\nrule Expr = Number \"+\" Number;"
        };

        var result = LowerGrammar(files, "root/main.storm");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Grammar);

        // The rule expression should reference the synthetic token
        var exprRule = result.Grammar!.Rules.FirstOrDefault(r => r.Name == "Expr");
        Assert.NotNull(exprRule);
        _ = Assert.Single(exprRule!.Alternatives);

        var alt = exprRule.Alternatives[0];
        var seq = Assert.IsType<SequenceIR>(alt.Expression);
        Assert.Equal(3, seq.Elements.Count);

        // First and third elements should be TokenRefIR to Number
        var numRef1 = Assert.IsType<TokenRefIR>(seq.Elements[0]);
        Assert.Equal("Number", numRef1.Token.Name);

        // Middle element should be a TokenRefIR pointing to the synthetic token
        var plusRef = Assert.IsType<TokenRefIR>(seq.Elements[1]);
        Assert.True(plusRef.Token.IsSynthetic);

        var numRef2 = Assert.IsType<TokenRefIR>(seq.Elements[2]);
        Assert.Equal("Number", numRef2.Token.Name);
    }

    // ---------------------------------------------------------------
    //  Declaration-order assignment
    // ---------------------------------------------------------------

    [Fact]
    public void Lower_TokenDeclarationOrder_AssignedSequentially()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\ntoken Foo = \"foo\";\ntoken Bar = \"bar\";\nhidden token WS = [ \\t\\n]+;\nrule Start = Foo Bar;"
        };

        var result = LowerGrammar(files, "root/main.storm");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Grammar);

        var foo = result.Grammar!.Tokens.First(t => t.Name == "Foo");
        var bar = result.Grammar.Tokens.First(t => t.Name == "Bar");
        var ws = result.Grammar.Tokens.First(t => t.Name == "WS");

        Assert.Equal(0, foo.DeclarationIndex);
        Assert.Equal(1, bar.DeclarationIndex);
        Assert.Equal(2, ws.DeclarationIndex);
    }

    [Fact]
    public void Lower_SyntheticTokens_AssignedAfterExplicitTokens()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\ntoken Number = [0-9]+;\ntoken Ident = [a-z]+;\nrule Expr = Number \"+\" Ident;"
        };

        var result = LowerGrammar(files, "root/main.storm");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Grammar);

        var number = result.Grammar!.Tokens.First(t => t.Name == "Number");
        var ident = result.Grammar.Tokens.First(t => t.Name == "Ident");
        var synthPlus = result.Grammar.Tokens.First(t => t.IsSynthetic);

        Assert.Equal(0, number.DeclarationIndex);
        Assert.Equal(1, ident.DeclarationIndex);
        Assert.True(synthPlus.DeclarationIndex > ident.DeclarationIndex);
    }

    [Fact]
    public void Lower_HiddenToken_PreservesHiddenFlag()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\ntoken Foo = \"foo\";\nhidden token WS = [ \\t]+;\nrule Start = Foo;"
        };

        var result = LowerGrammar(files, "root/main.storm");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Grammar);

        var foo = result.Grammar!.Tokens.First(t => t.Name == "Foo");
        var ws = result.Grammar.Tokens.First(t => t.Name == "WS");

        Assert.False(foo.IsHidden);
        Assert.True(ws.IsHidden);
    }

    // ---------------------------------------------------------------
    //  Reference resolution
    // ---------------------------------------------------------------

    [Fact]
    public void Lower_RuleReferencesToken_ResolvesToTokenRefIR()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\ntoken Number = [0-9]+;\nrule Start = Number;"
        };

        var result = LowerGrammar(files, "root/main.storm");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Grammar);

        var startRule = result.Grammar!.Rules.First(r => r.Name == "Start");
        _ = Assert.Single(startRule.Alternatives);

        var tokenRef = Assert.IsType<TokenRefIR>(startRule.Alternatives[0].Expression);
        Assert.Same(result.Grammar.Tokens.First(t => t.Name == "Number"), tokenRef.Token);
    }

    [Fact]
    public void Lower_RuleReferencesRule_ResolvesToRuleRefIR()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\ntoken Number = [0-9]+;\nrule Expr = Number;\nrule Start = Expr;"
        };

        var result = LowerGrammar(files, "root/main.storm");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Grammar);

        var startRule = result.Grammar!.Rules.First(r => r.Name == "Start");
        _ = Assert.Single(startRule.Alternatives);

        var ruleRef = Assert.IsType<RuleRefIR>(startRule.Alternatives[0].Expression);
        Assert.Equal("Expr", ruleRef.Rule.Name);
    }

    [Fact]
    public void Lower_RuleReferencesPointToCorrectIRObjects()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\ntoken Number = [0-9]+;\ntoken Ident = [a-z]+;\nrule Expr = Number | Ident;\nrule Start = Expr;"
        };

        var result = LowerGrammar(files, "root/main.storm");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Grammar);

        var exprRule = result.Grammar!.Rules.First(r => r.Name == "Expr");
        var startRule = result.Grammar.Rules.First(r => r.Name == "Start");

        // Start references Expr — the RuleRefIR should point to the same RuleIR object
        var startRef = Assert.IsType<RuleRefIR>(startRule.Alternatives[0].Expression);
        Assert.Equal("Expr", startRef.Rule.Name);

        // Expr has two alternatives, each referencing a token
        Assert.Equal(2, exprRule.Alternatives.Count);
        var numRef = Assert.IsType<TokenRefIR>(exprRule.Alternatives[0].Expression);
        var identRef = Assert.IsType<TokenRefIR>(exprRule.Alternatives[1].Expression);

        Assert.Same(result.Grammar.Tokens.First(t => t.Name == "Number"), numRef.Token);
        Assert.Same(result.Grammar.Tokens.First(t => t.Name == "Ident"), identRef.Token);
    }

    // ---------------------------------------------------------------
    //  Named alternatives and field labels
    // ---------------------------------------------------------------

    [Fact]
    public void Lower_NamedAlternatives_PreservedInRuleIR()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\ntoken Number = [0-9]+;\ntoken Ident = [a-z]+;\nrule Expr = Number as Literal | Ident as Variable;"
        };

        var result = LowerGrammar(files, "root/main.storm");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Grammar);

        var exprRule = result.Grammar!.Rules.First(r => r.Name == "Expr");
        Assert.Equal(2, exprRule.Alternatives.Count);
        Assert.Equal("Literal", exprRule.Alternatives[0].AlternativeName);
        Assert.Equal("Variable", exprRule.Alternatives[1].AlternativeName);
    }

    [Fact]
    public void Lower_FieldLabels_CollectedInAlternative()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\ntoken Number = [0-9]+;\nrule Expr = value:Number;"
        };

        var result = LowerGrammar(files, "root/main.storm");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Grammar);

        var exprRule = result.Grammar!.Rules.First(r => r.Name == "Expr");
        _ = Assert.Single(exprRule.Alternatives);

        var alt = exprRule.Alternatives[0];
        _ = Assert.Single(alt.FieldLabels);
        Assert.Equal("value", alt.FieldLabels[0].Label);
        _ = Assert.IsType<TokenRefIR>(alt.FieldLabels[0].Expression);
    }

    // ---------------------------------------------------------------
    //  Overall structure
    // ---------------------------------------------------------------

    [Fact]
    public void Lower_CompleteGrammar_ProducesCorrectCounts()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\nfragment Digit = [0-9];\ntoken Number = Digit+;\ntoken Ident = [a-zA-Z]+;\nhidden token WS = [ \\t\\n]+;\nrule Expr = Number \"+\" Ident | Ident;\nrule Start = Expr;"
        };

        var result = LowerGrammar(files, "root/main.storm");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Grammar);

        // 3 explicit tokens + 1 synthetic for "+"
        Assert.Equal(4, result.Grammar!.Tokens.Count);
        // 2 rules
        Assert.Equal(2, result.Grammar.Rules.Count);
    }

    [Fact]
    public void Lower_EmptyGrammar_Succeeds()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\n"
        };

        var result = LowerGrammar(files, "root/main.storm");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Grammar);
        Assert.Empty(result.Grammar!.Tokens);
        Assert.Empty(result.Grammar.Rules);
    }

    // ---------------------------------------------------------------
    //  Regex literal IR lowering
    // ---------------------------------------------------------------

    [Fact]
    public void Lower_TokenWithRegexLiteralBody_ProducesRegexLiteralIR()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\ntoken Number = `[0-9]+`;\nrule Start = Number;"
        };

        var result = LowerGrammar(files, "root/main.storm");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Grammar);

        var numberToken = result.Grammar!.Tokens.FirstOrDefault(t => t.Name == "Number");
        Assert.NotNull(numberToken);

        var regexLiteral = Assert.IsType<RegexLiteralIR>(numberToken!.Expression);
        Assert.Equal("[0-9]+", regexLiteral.Pattern);
    }

    [Fact]
    public void Lower_RuleReferencingTokenWithRegexLiteral_CorrectIRStructure()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\ntoken Ident = `[a-zA-Z_][a-zA-Z0-9_]*`;\nrule Start = Ident;"
        };

        var result = LowerGrammar(files, "root/main.storm");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Grammar);

        // The token should have a RegexLiteralIR expression
        var identToken = result.Grammar!.Tokens.FirstOrDefault(t => t.Name == "Ident");
        Assert.NotNull(identToken);
        var regexLiteral = Assert.IsType<RegexLiteralIR>(identToken!.Expression);
        Assert.Equal("[a-zA-Z_][a-zA-Z0-9_]*", regexLiteral.Pattern);

        // The rule should reference the token via TokenRefIR
        var startRule = result.Grammar.Rules.FirstOrDefault(r => r.Name == "Start");
        Assert.NotNull(startRule);
        _ = Assert.Single(startRule!.Alternatives);
        var tokenRef = Assert.IsType<TokenRefIR>(startRule.Alternatives[0].Expression);
        Assert.Same(identToken, tokenRef.Token);
    }

    // ---------------------------------------------------------------
    //  @memo annotation propagation through lowering
    // ---------------------------------------------------------------

    [Fact]
    public void Lower_MemoAnnotatedRule_ProducesRuleIRWithIsMemoTrue()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\ntoken Ident = [a-z]+;\n@memo rule Start = Ident;"
        };

        var result = LowerGrammar(files, "root/main.storm");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Grammar);

        var startRule = result.Grammar!.Rules.First(r => r.Name == "Start");
        Assert.True(startRule.IsMemo, "Rule annotated with @memo should have IsMemo = true");
    }

    [Fact]
    public void Lower_RuleWithoutMemoAnnotation_ProducesRuleIRWithIsMemoFalse()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\ntoken Ident = [a-z]+;\nrule Start = Ident;"
        };

        var result = LowerGrammar(files, "root/main.storm");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Grammar);

        var startRule = result.Grammar!.Rules.First(r => r.Name == "Start");
        Assert.False(startRule.IsMemo, "Rule without @memo should have IsMemo = false");
    }

    [Fact]
    public void Lower_MixedMemoAnnotations_PropagatesCorrectly()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\ntoken Ident = [a-z]+;\ntoken Num = [0-9]+;\n@memo rule Expr = Ident | Num;\nrule Term = Ident;\n@memo rule Factor = Num;"
        };

        var result = LowerGrammar(files, "root/main.storm");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Grammar);

        var exprRule = result.Grammar!.Rules.First(r => r.Name == "Expr");
        var termRule = result.Grammar.Rules.First(r => r.Name == "Term");
        var factorRule = result.Grammar.Rules.First(r => r.Name == "Factor");

        Assert.True(exprRule.IsMemo, "Expr (annotated with @memo) should have IsMemo = true");
        Assert.False(termRule.IsMemo, "Term (no annotation) should have IsMemo = false");
        Assert.True(factorRule.IsMemo, "Factor (annotated with @memo) should have IsMemo = true");
    }
}