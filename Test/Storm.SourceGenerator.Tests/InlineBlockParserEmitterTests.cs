using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Unit tests for parser emitter inline block code generation.
/// Validates: Requirements 7.1, 7.2, 7.3, 7.4, 7.5, 7.10
/// </summary>
public class InlineBlockParserEmitterTests
{
    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

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

    private static EmissionContext CreateContext(string grammar, string grammarName = "Test", string ns = "Test.Generated")
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar " + grammarName + ";\n" + grammar
        };

        var resolver = new StormImportResolver(InMemoryFileReader(files));
        var resolution = resolver.Resolve("root/main.storm");
        var stormGrammar = StormGrammar.Build(resolution);
        var loweringResult = GrammarIRLowering.Lower(stormGrammar);

        Assert.True(loweringResult.Succeeded, "Grammar lowering failed: " +
            string.Join(", ", loweringResult.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(loweringResult.Grammar);

        return new EmissionContext(ns, grammarName, loweringResult.Grammar!);
    }

    /// <summary>
    /// Builds a grammar with a simple inline block: two arms diverging on different tokens
    /// after a shared prefix.
    /// </summary>
    private static string SimpleInlineGrammar()
    {
        return """
            token A = "a";
            token B = "b";
            token C = "c";
            token D = "d";
            rule Prefix = A B;
            rule Arm1 = Prefix C;
            rule Arm2 = Prefix D;
            rule Parent = inline Prefix: { Arm1 as First, Arm2 as Second };
            """;
    }

    /// <summary>
    /// Builds a grammar with an inline block alongside another alternative,
    /// exercising the multi-alt + inline block combination.
    /// </summary>
    private static string InlineBlockWithSiblingAltGrammar()
    {
        return """
            token A = "a";
            token B = "b";
            token C = "c";
            token D = "d";
            token E = "e";
            rule Prefix = A B;
            rule Arm1 = Prefix C;
            rule Arm2 = Prefix D;
            rule Parent = E as Plain | inline Prefix: { Arm1 as First, Arm2 as Second };
            """;
    }

    /// <summary>
    /// Builds a grammar with labeled prefix and tail fields.
    /// </summary>
    private static string LabeledInlineGrammar()
    {
        return """
            token A = "a";
            token B = "b";
            token C = "c";
            token D = "d";
            rule Prefix = first:A second:B;
            rule Arm1 = head:Prefix tail:C;
            rule Arm2 = head:Prefix tail:D;
            rule Parent = inline Prefix: { head:Arm1 as First, head:Arm2 as Second };
            """;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Tests: Save/Restore pattern present (multi-alt context)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void EmitInlineBlock_MultiAlt_SavesPosition_BeforeAlternatives()
    {
        var ctx = CreateContext(InlineBlockWithSiblingAltGrammar());
        var output = ParserEmitter.Emit(ctx);

        // Multi-alt with inline block should emit the PEG ordered-choice
        // save position pattern
        Assert.Contains("var saved = _pos;", output);
    }

    [Fact]
    public void EmitInlineBlock_MultiAlt_RestoresPosition_OnAllAlternativesExhausted()
    {
        var ctx = CreateContext(InlineBlockWithSiblingAltGrammar());
        var output = ParserEmitter.Emit(ctx);

        // After all alternatives fail, position is restored
        Assert.Contains("_pos = saved;", output);
    }

    [Fact]
    public void EmitInlineBlock_MultiAlt_SavesErrorState_ForBacktracking()
    {
        var ctx = CreateContext(InlineBlockWithSiblingAltGrammar());
        var output = ParserEmitter.Emit(ctx);

        // Multi-alt backtracking saves error state
        Assert.Contains("var savedError = _hasError;", output);
        Assert.Contains("var savedDiagCount = _diagnostics?.Count ?? 0;", output);
    }

    [Fact]
    public void EmitInlineBlock_SingleAlt_SavesSpPosition()
    {
        var ctx = CreateContext(SimpleInlineGrammar());
        var output = ParserEmitter.Emit(ctx);

        // Single-alt inline block path uses sp position variable
        Assert.Contains("var sp = _pos;", output);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Tests: Prefix parsed once
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void EmitInlineBlock_ParsesPrefixOnce_InlineOkVariable()
    {
        var ctx = CreateContext(SimpleInlineGrammar());
        var output = ParserEmitter.Emit(ctx);

        // The inline block uses _inlineOk to track prefix success
        Assert.Contains("var _inlineOk = true;", output);
    }

    [Fact]
    public void EmitInlineBlock_PrefixGatesArmDispatch()
    {
        var ctx = CreateContext(SimpleInlineGrammar());
        var output = ParserEmitter.Emit(ctx);

        // After prefix parsed, dispatch is gated on success
        Assert.Contains("if (_inlineOk && !_hasError)", output);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Tests: Prediction dispatch present
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void EmitInlineBlock_PeeksNextToken_ForPrediction()
    {
        var ctx = CreateContext(SimpleInlineGrammar());
        var output = ParserEmitter.Emit(ctx);

        // Prediction peeks at next token
        Assert.Contains("var _peek = Peek();", output);
    }

    [Fact]
    public void EmitInlineBlock_DispatchesOnTokenKind_IfBranches()
    {
        var ctx = CreateContext(SimpleInlineGrammar());
        var output = ParserEmitter.Emit(ctx);

        // Dispatch uses if/else if branches comparing _peek to token kinds
        Assert.Contains("if (_peek == (int)", output);
    }

    [Fact]
    public void EmitInlineBlock_DispatchBranches_ForEachDistinctFirstToken()
    {
        var ctx = CreateContext(SimpleInlineGrammar());
        var output = ParserEmitter.Emit(ctx);

        // Arm1's tail starts with C, Arm2's tail starts with D
        // Both should appear in dispatch branches
        Assert.Contains("TokenKind.C)", output);
        Assert.Contains("TokenKind.D)", output);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Tests: Flat node construction with prefix + tail fields
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void EmitInlineBlock_ConstructsFlatNode_WithArmTypeName()
    {
        var ctx = CreateContext(SimpleInlineGrammar());
        var output = ParserEmitter.Emit(ctx);

        // Each arm constructs a node named {AltName}{RuleName}Node
        Assert.Contains("new FirstParentNode()", output);
        Assert.Contains("new SecondParentNode()", output);
    }

    [Fact]
    public void EmitInlineBlock_AssignsPrefixFields_ToNode()
    {
        var ctx = CreateContext(LabeledInlineGrammar());
        var output = ParserEmitter.Emit(ctx);

        // Prefix fields are assigned to the node
        // The prefix has labeled fields "first" and "second"
        Assert.Contains("node.First", output);
        Assert.Contains("node.Second", output);
    }

    [Fact]
    public void EmitInlineBlock_AssignsTailFields_ToNode()
    {
        var ctx = CreateContext(LabeledInlineGrammar());
        var output = ParserEmitter.Emit(ctx);

        // Tail fields are also assigned to the node
        // The tail has a labeled field "tail"
        Assert.Contains("node.Tail", output);
    }

    [Fact]
    public void EmitInlineBlock_TailParseTrackedSeparately()
    {
        var ctx = CreateContext(SimpleInlineGrammar());
        var output = ParserEmitter.Emit(ctx);

        // Tail parsing has its own success tracking variable
        Assert.Contains("var _tailOk = true;", output);
    }

    [Fact]
    public void EmitInlineBlock_TailSuccessGatesNodeConstruction()
    {
        var ctx = CreateContext(SimpleInlineGrammar());
        var output = ParserEmitter.Emit(ctx);

        // Node construction is gated on tail success
        Assert.Contains("if (_tailOk && !_hasError)", output);
    }

    [Fact]
    public void EmitInlineBlock_NodeHasSpanAssignment()
    {
        var ctx = CreateContext(SimpleInlineGrammar());
        var output = ParserEmitter.Emit(ctx);

        // Node gets its source span assigned
        Assert.Contains("node.Span = Span(", output);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Tests: Code structure completeness
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void EmitInlineBlock_WithSiblingAlt_EmitsBothPatternsInSameMethod()
    {
        var ctx = CreateContext(InlineBlockWithSiblingAltGrammar());
        var output = ParserEmitter.Emit(ctx);

        // The method should contain both a plain alternative pattern
        // and the inline block pattern
        Assert.Contains("new PlainParentNode()", output);
        Assert.Contains("var _inlineOk = true;", output);
        Assert.Contains("var _peek = Peek();", output);
    }

    [Fact]
    public void EmitInlineBlock_GeneratesParseMethodForParentRule()
    {
        var ctx = CreateContext(SimpleInlineGrammar());
        var output = ParserEmitter.Emit(ctx);

        // A parse method is generated for the parent rule
        Assert.Contains("ParseParent", output);
    }

    [Fact]
    public void EmitInlineBlock_ReturnsNodeOnSuccess()
    {
        var ctx = CreateContext(SimpleInlineGrammar());
        var output = ParserEmitter.Emit(ctx);

        // After constructing the node, the method returns it
        Assert.Contains("return node;", output);
    }
}