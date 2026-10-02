using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Unit and property-based tests for IR lowering of inline blocks.
/// Validates: Requirements 6.1, 6.2, 6.3, 6.5
/// </summary>
public class InlineBlockIRLoweringTests
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

    private static GrammarIRLowering.LoweringResult LowerGrammar(string source)
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\n" + source,
        };
        var resolver = new StormImportResolver(InMemoryFileReader(files));
        var resolution = resolver.Resolve("root/main.storm");
        var grammar = StormGrammar.Build(resolution);
        return GrammarIRLowering.Lower(grammar);
    }

    /// <summary>
    /// Finds the InlineBlockIR within the given rule's alternatives.
    /// </summary>
    private static InlineBlockIR FindInlineBlock(GrammarIR grammar, string ruleName)
    {
        var rule = grammar.Rules.First(r => r.Name == ruleName);
        foreach(var alt in rule.Alternatives)
        {
            if(alt.Expression is InlineBlockIR inlineBlock)
            {
                return inlineBlock;
            }
        }

        throw new InvalidOperationException(
            $"No InlineBlockIR found in rule '{ruleName}'. " +
            $"Alternatives: {string.Join(", ", rule.Alternatives.Select(a => a.Expression.GetType().Name))}");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Prefix elements correctly extracted
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Lower_InlineBlock_PrefixElementsExtracted_SingleToken()
    {
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            rule Prefix = A;
            rule Arm1 = Prefix B;
            rule Arm2 = Prefix C;
            rule R = inline Prefix: { Arm1 as Alt1, Arm2 as Alt2 };
            """;

        var result = LowerGrammar(source);

        Assert.True(result.Succeeded, "Lowering failed: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var block = FindInlineBlock(result.Grammar!, "R");

        // Prefix has a single element: TokenRefIR to "A"
        _ = Assert.Single(block.PrefixElements);
        var prefixToken = Assert.IsType<TokenRefIR>(block.PrefixElements[0]);
        Assert.Equal("A", prefixToken.Token.Name);
    }

    [Fact]
    public void Lower_InlineBlock_PrefixElementsExtracted_MultipleTokens()
    {
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            token D = "d";
            rule Prefix = A B C;
            rule Arm1 = Prefix D;
            rule R = inline Prefix: { Arm1 as Alt1 };
            """;

        var result = LowerGrammar(source);

        Assert.True(result.Succeeded, "Lowering failed: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var block = FindInlineBlock(result.Grammar!, "R");

        // Prefix has three elements: A, B, C
        Assert.Equal(3, block.PrefixElements.Count);
        var el0 = Assert.IsType<TokenRefIR>(block.PrefixElements[0]);
        var el1 = Assert.IsType<TokenRefIR>(block.PrefixElements[1]);
        var el2 = Assert.IsType<TokenRefIR>(block.PrefixElements[2]);
        Assert.Equal("A", el0.Token.Name);
        Assert.Equal("B", el1.Token.Name);
        Assert.Equal("C", el2.Token.Name);
    }

    [Fact]
    public void Lower_InlineBlock_PrefixElementsExtracted_MixedTokensAndRules()
    {
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            rule SubRule = A;
            rule Prefix = SubRule B;
            rule Arm1 = Prefix C;
            rule R = inline Prefix: { Arm1 as Alt1 };
            """;

        var result = LowerGrammar(source);

        Assert.True(result.Succeeded, "Lowering failed: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var block = FindInlineBlock(result.Grammar!, "R");

        // Prefix has two elements: RuleRefIR to SubRule, TokenRefIR to B
        Assert.Equal(2, block.PrefixElements.Count);
        var ruleRef = Assert.IsType<RuleRefIR>(block.PrefixElements[0]);
        Assert.Equal("SubRule", ruleRef.Rule.Name);
        var tokenRef = Assert.IsType<TokenRefIR>(block.PrefixElements[1]);
        Assert.Equal("B", tokenRef.Token.Name);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Arm tail expressions have prefix stripped
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Lower_InlineBlock_ArmTailHasPrefixStripped_SingleTailElement()
    {
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            rule Prefix = A;
            rule Arm1 = Prefix B;
            rule Arm2 = Prefix C;
            rule R = inline Prefix: { Arm1 as Alt1, Arm2 as Alt2 };
            """;

        var result = LowerGrammar(source);

        Assert.True(result.Succeeded, "Lowering failed: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var block = FindInlineBlock(result.Grammar!, "R");

        Assert.Equal(2, block.Arms.Count);

        // Arm1's rule body is "Prefix B" → tail is just B
        var tail0 = Assert.IsType<TokenRefIR>(block.Arms[0].TailExpression);
        Assert.Equal("B", tail0.Token.Name);

        // Arm2's rule body is "Prefix C" → tail is just C
        var tail1 = Assert.IsType<TokenRefIR>(block.Arms[1].TailExpression);
        Assert.Equal("C", tail1.Token.Name);
    }

    [Fact]
    public void Lower_InlineBlock_ArmTailHasPrefixStripped_MultipleTailElements()
    {
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            token D = "d";
            rule Prefix = A;
            rule Arm1 = Prefix B C D;
            rule R = inline Prefix: { Arm1 as Alt1 };
            """;

        var result = LowerGrammar(source);

        Assert.True(result.Succeeded, "Lowering failed: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var block = FindInlineBlock(result.Grammar!, "R");

        _ = Assert.Single(block.Arms);

        // Arm1's rule body is "Prefix B C D" → tail is SequenceIR(B, C, D)
        var tailSeq = Assert.IsType<SequenceIR>(block.Arms[0].TailExpression);
        Assert.Equal(3, tailSeq.Elements.Count);
        Assert.Equal("B", Assert.IsType<TokenRefIR>(tailSeq.Elements[0]).Token.Name);
        Assert.Equal("C", Assert.IsType<TokenRefIR>(tailSeq.Elements[1]).Token.Name);
        Assert.Equal("D", Assert.IsType<TokenRefIR>(tailSeq.Elements[2]).Token.Name);
    }

    [Fact]
    public void Lower_InlineBlock_ArmTailStripped_LabeledPrefixRef()
    {
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            rule Prefix = A B;
            rule Arm1 = head:Prefix C;
            rule R = inline Prefix: { Arm1 as Alt1 };
            """;

        var result = LowerGrammar(source);

        Assert.True(result.Succeeded, "Lowering failed: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var block = FindInlineBlock(result.Grammar!, "R");

        _ = Assert.Single(block.Arms);

        // Even though the prefix ref is labeled "head:", the tail should still be C
        var tail = Assert.IsType<TokenRefIR>(block.Arms[0].TailExpression);
        Assert.Equal("C", tail.Token.Name);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Empty tail case (arm is pure prefix)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Lower_InlineBlock_EmptyTail_ArmIsPurePrefix()
    {
        var source = """
            token A = "a";
            token B = "b";
            rule Prefix = A;
            rule Arm1 = Prefix;
            rule Arm2 = Prefix B;
            rule R = inline Prefix: { Arm1 as Alt1, Arm2 as Alt2 };
            """;

        var result = LowerGrammar(source);

        Assert.True(result.Succeeded, "Lowering failed: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var block = FindInlineBlock(result.Grammar!, "R");

        Assert.Equal(2, block.Arms.Count);

        // Arm1's rule body is just "Prefix" → tail is empty SequenceIR
        var emptyTail = Assert.IsType<SequenceIR>(block.Arms[0].TailExpression);
        Assert.Empty(emptyTail.Elements);

        // Arm2 should still have a valid tail
        var tail1 = Assert.IsType<TokenRefIR>(block.Arms[1].TailExpression);
        Assert.Equal("B", tail1.Token.Name);
    }

    [Fact]
    public void Lower_InlineBlock_EmptyTail_LabeledPrefixOnly()
    {
        var source = """
            token A = "a";
            token B = "b";
            rule Prefix = A B;
            rule Arm1 = head:Prefix;
            rule Arm2 = Prefix B;
            rule R = inline Prefix: { Arm1 as Alt1, Arm2 as Alt2 };
            """;

        var result = LowerGrammar(source);

        Assert.True(result.Succeeded, "Lowering failed: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var block = FindInlineBlock(result.Grammar!, "R");

        // Arm1 has only the labeled prefix ref → empty tail
        var emptyTail = Assert.IsType<SequenceIR>(block.Arms[0].TailExpression);
        Assert.Empty(emptyTail.Elements);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Label preservation on prefix and tail
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Lower_InlineBlock_PrefixLabels_Preserved()
    {
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            rule Prefix = name:A value:B;
            rule Arm1 = Prefix C;
            rule R = inline Prefix: { Arm1 as Alt1 };
            """;

        var result = LowerGrammar(source);

        Assert.True(result.Succeeded, "Lowering failed: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var block = FindInlineBlock(result.Grammar!, "R");

        // PrefixLabels should contain "name" and "value"
        Assert.Equal(2, block.PrefixLabels.Count);
        Assert.Equal("name", block.PrefixLabels[0].Label);
        Assert.Equal("value", block.PrefixLabels[1].Label);

        // PrefixLabels' expressions should point to the correct token refs
        var nameExpr = Assert.IsType<TokenRefIR>(block.PrefixLabels[0].Expression);
        Assert.Equal("A", nameExpr.Token.Name);
        var valueExpr = Assert.IsType<TokenRefIR>(block.PrefixLabels[1].Expression);
        Assert.Equal("B", valueExpr.Token.Name);
    }

    [Fact]
    public void Lower_InlineBlock_TailLabels_Preserved()
    {
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            rule Prefix = A;
            rule Arm1 = Prefix result:B extra:C;
            rule R = inline Prefix: { Arm1 as Alt1 };
            """;

        var result = LowerGrammar(source);

        Assert.True(result.Succeeded, "Lowering failed: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var block = FindInlineBlock(result.Grammar!, "R");

        _ = Assert.Single(block.Arms);

        // TailLabels should contain "result" and "extra"
        Assert.Equal(2, block.Arms[0].TailLabels.Count);
        Assert.Equal("result", block.Arms[0].TailLabels[0].Label);
        Assert.Equal("extra", block.Arms[0].TailLabels[1].Label);

        // Verify tail label expressions
        var resultExpr = Assert.IsType<TokenRefIR>(block.Arms[0].TailLabels[0].Expression);
        Assert.Equal("B", resultExpr.Token.Name);
        var extraExpr = Assert.IsType<TokenRefIR>(block.Arms[0].TailLabels[1].Expression);
        Assert.Equal("C", extraExpr.Token.Name);
    }

    [Fact]
    public void Lower_InlineBlock_PrefixRefLabel_NotInTailLabels()
    {
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            rule Prefix = A B;
            rule Arm1 = head:Prefix tail:C;
            rule R = inline Prefix: { Arm1 as Alt1 };
            """;

        var result = LowerGrammar(source);

        Assert.True(result.Succeeded, "Lowering failed: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var block = FindInlineBlock(result.Grammar!, "R");

        _ = Assert.Single(block.Arms);

        // The label "head" was on the prefix reference (first element, stripped).
        // TailLabels should only contain "tail", not "head".
        _ = Assert.Single(block.Arms[0].TailLabels);
        Assert.Equal("tail", block.Arms[0].TailLabels[0].Label);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Arm metadata preservation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Lower_InlineBlock_ArmAlternativeNames_Preserved()
    {
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            rule Prefix = A;
            rule Arm1 = Prefix B;
            rule Arm2 = Prefix C;
            rule R = inline Prefix: { Arm1 as MyFirst, Arm2 as MySecond };
            """;

        var result = LowerGrammar(source);

        Assert.True(result.Succeeded, "Lowering failed: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var block = FindInlineBlock(result.Grammar!, "R");

        Assert.Equal(2, block.Arms.Count);
        Assert.Equal("MyFirst", block.Arms[0].AlternativeName);
        Assert.Equal("MySecond", block.Arms[1].AlternativeName);
    }

    [Fact]
    public void Lower_InlineBlock_ArmLabels_Preserved()
    {
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            rule Prefix = A;
            rule Arm1 = Prefix B;
            rule Arm2 = Prefix C;
            rule R = inline Prefix: { first:Arm1 as Alt1, second:Arm2 as Alt2 };
            """;

        var result = LowerGrammar(source);

        Assert.True(result.Succeeded, "Lowering failed: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var block = FindInlineBlock(result.Grammar!, "R");

        Assert.Equal(2, block.Arms.Count);
        Assert.Equal("first", block.Arms[0].Label);
        Assert.Equal("second", block.Arms[1].Label);
    }

    [Fact]
    public void Lower_InlineBlock_ArmTargetRule_Resolved()
    {
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            rule Prefix = A;
            rule Arm1 = Prefix B;
            rule Arm2 = Prefix C;
            rule R = inline Prefix: { Arm1 as Alt1, Arm2 as Alt2 };
            """;

        var result = LowerGrammar(source);

        Assert.True(result.Succeeded, "Lowering failed: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var block = FindInlineBlock(result.Grammar!, "R");

        Assert.Equal("Arm1", block.Arms[0].TargetRule.Name);
        Assert.Equal("Arm2", block.Arms[1].TargetRule.Name);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: InlineBlockIR wrapped in RuleAlternativeIR with null name
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Lower_InlineBlock_WrappedInRuleAlternativeIR_NullAltName()
    {
        var source = """
            token A = "a";
            token B = "b";
            rule Prefix = A;
            rule Arm1 = Prefix B;
            rule R = inline Prefix: { Arm1 as Alt1 };
            """;

        var result = LowerGrammar(source);

        Assert.True(result.Succeeded, "Lowering failed: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var rule = result.Grammar!.Rules.First(r => r.Name == "R");
        _ = Assert.Single(rule.Alternatives);
        Assert.Null(rule.Alternatives[0].AlternativeName);
        _ = Assert.IsType<InlineBlockIR>(rule.Alternatives[0].Expression);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Property 10: IR Lowering - Prefix Stripping by First-Element Removal
    //  Feature: inline-rule-prefix, Property 10: IR Lowering — Prefix Stripping by First-Element Removal
    //
    //  For any valid inline block, after IR lowering, each arm's tail
    //  expression equals the arm's rule body with its first element removed.
    //  The prefix elements contain the lowered prefix rule's body elements.
    //  **Validates: Requirements 6.1, 6.2, 6.3, 6.5**
    // ═══════════════════════════════════════════════════════════════════

    private static Gen<string> GenIdentifier()
    {
        var startChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();
        var contChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

        return Gen.Elements(startChars).SelectMany(start =>
            Gen.Choose(1, 5).SelectMany(len =>
                Gen.ArrayOf(Gen.Elements(contChars), len)
                    .Select(chars => start + new string(chars))));
    }

    [Property(MaxTest = 60)]
    public Property P10_PrefixStripping_FirstElementRemoval()
    {
        // Generate grammars with:
        // - A prefix rule with 1-3 token elements
        // - 1-3 arms each with the prefix ref + 1-3 tail token elements
        // Verify: prefix elements count matches prefix rule body,
        //         arm tail element count = arm body count - 1
        var testGen =
            GenIdentifier().SelectMany(prefixName =>
            Gen.Choose(1, 3).SelectMany(prefixTokenCount =>
            Gen.ListOf(GenIdentifier(), prefixTokenCount).SelectMany(prefixTokenNames =>
            Gen.Choose(1, 3).SelectMany(armCount =>
            Gen.ListOf(Gen.Choose(1, 3), armCount).SelectMany(tailCounts =>
            Gen.ListOf(GenIdentifier(), armCount).SelectMany(armNames =>
            Gen.ListOf(GenIdentifier(), armCount).Select(altNames =>
                (prefixName, prefixTokenNames: prefixTokenNames.ToList(),
                 armCount, tailCounts: tailCounts.ToList(),
                 armNames: armNames.ToList(), altNames: altNames.ToList()))))))));

        return Prop.ForAll(testGen.ToArbitrary(), drawn =>
        {
            var (prefixName, prefixTokenNames, armCount, tailCounts, armNames, altNames) = drawn;
            // Build unique names to avoid collisions
            var allNames = new HashSet<string>(StringComparer.Ordinal) { prefixName };
            var validPrefixTokens = new List<string>();
            foreach(var name in prefixTokenNames)
            {
                if(allNames.Add(name))
                {
                    validPrefixTokens.Add(name);
                }
            }

            if(validPrefixTokens.Count == 0)
            {
                return;
            }

            var validArms = new List<(string armName, string altName, List<string> tailTokens)>();
            for(var i = 0; i < armCount; i++)
            {
                var armName = armNames[i];
                var altName = altNames[i];
                if(!allNames.Add(armName) || !allNames.Add(altName))
                {
                    continue;
                }

                var tailTokens = new List<string>();
                for(var j = 0; j < tailCounts[i]; j++)
                {
                    var tokenName = $"T{i}x{j}";
                    if(allNames.Add(tokenName))
                    {
                        tailTokens.Add(tokenName);
                    }
                }

                if(tailTokens.Count == 0)
                {
                    continue;
                }

                validArms.Add((armName, altName, tailTokens));
            }

            if(validArms.Count == 0)
            {
                return;
            }

            // Build grammar source
            var source = new System.Text.StringBuilder();

            // Declare prefix tokens
            foreach(var tok in validPrefixTokens)
            {
                _ = source.AppendLine($"token {tok} = \"{tok.ToLowerInvariant()}\";");
            }

            // Declare tail tokens
            foreach(var (_, _, tailTokens) in validArms)
            {
                foreach(var tok in tailTokens)
                {
                    _ = source.AppendLine($"token {tok} = \"{tok.ToLowerInvariant()}\";");
                }
            }

            // Prefix rule
            _ = source.AppendLine($"rule {prefixName} = {string.Join(" ", validPrefixTokens)};");

            // Arm rules (prefix + tail tokens)
            foreach(var (armName, _, tailTokens) in validArms)
            {
                _ = source.AppendLine($"rule {armName} = {prefixName} {string.Join(" ", tailTokens)};");
            }

            // Container rule with inline block
            var armsList = string.Join(", ", validArms.Select(a => $"{a.armName} as {a.altName}"));
            _ = source.AppendLine($"rule Container = inline {prefixName}: {{ {armsList} }};");

            var result = LowerGrammar(source.ToString());

            Assert.True(result.Succeeded,
                $"Lowering failed: {string.Join("; ", result.Diagnostics.Select(d => d.Message))}\nSource:\n{source}");

            var block = FindInlineBlock(result.Grammar!, "Container");

            // Property assertion 1: prefix elements count matches prefix rule body element count
            Assert.Equal(validPrefixTokens.Count, block.PrefixElements.Count);

            // Property assertion 2: each arm's tail has the correct number of elements
            for(var i = 0; i < validArms.Count; i++)
            {
                var expectedTailCount = validArms[i].tailTokens.Count;
                var arm = block.Arms[i];

                if(expectedTailCount == 1)
                {
                    // Single tail element is not wrapped in SequenceIR
                    _ = Assert.IsType<TokenRefIR>(arm.TailExpression);
                }
                else
                {
                    var tailSeq = Assert.IsType<SequenceIR>(arm.TailExpression);
                    Assert.Equal(expectedTailCount, tailSeq.Elements.Count);
                }

                // Verify arm metadata
                Assert.Equal(validArms[i].altName, arm.AlternativeName);
                Assert.Equal(validArms[i].armName, arm.TargetRule.Name);
            }
        });
    }
}