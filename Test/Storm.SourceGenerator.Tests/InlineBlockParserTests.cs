using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Unit and property-based tests for inline block parsing in the Storm parser.
/// Validates: Requirements 2.1, 2.2, 2.3, 2.4, 2.5, 2.6, 2.7, 2.8, 2.9
/// </summary>
public class InlineBlockParserTests
{
    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static StormFile Parse(string input, out List<StormDiagnostic> diagnostics)
    {
        diagnostics = new List<StormDiagnostic>();
        var source = new StormSourceText("test.storm", input);
        var tokens = StormLexer.Tokenize(source, diagnostics);
        return StormParser.Parse(tokens, source, diagnostics);
    }

    /// <summary>
    /// Parses a rule declaration and extracts the inline block expression from the rule body.
    /// Expects the rule body to be an alternation with the inline block at index 0,
    /// or the inline block itself if it's the only expression.
    /// </summary>
    private static StormInlineBlockExpr ParseInlineBlock(string ruleSource, out List<StormDiagnostic> diagnostics)
    {
        var file = Parse(ruleSource, out diagnostics);
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);

        // The inline block can appear directly as the body,
        // or inside a named alternative, or as part of an alternation
        if(decl.Body is StormInlineBlockExpr inlineBlock)
        {
            return inlineBlock;
        }

        if(decl.Body is StormAlternationExpr alt)
        {
            // Find the first inline block in the alternation
            foreach(var alternative in alt.Alternatives)
            {
                if(alternative is StormInlineBlockExpr ib)
                {
                    return ib;
                }

                if(alternative is StormNamedAlternative { Expression: StormInlineBlockExpr namedIb })
                {
                    return namedIb;
                }
            }
        }

        throw new InvalidOperationException("Could not find StormInlineBlockExpr in parsed rule body");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Valid inline block with 1 arm
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Parse_InlineBlock_SingleArm()
    {
        var source = "rule Foo = inline Prefix: { RuleA as AltA };";
        var block = ParseInlineBlock(source, out var diags);

        Assert.Empty(diags);
        var prefix = Assert.IsType<StormReferenceExpr>(block.Prefix);
        Assert.Equal("Prefix", prefix.Name);
        _ = Assert.Single(block.Arms);

        var arm = block.Arms[0];
        var armExpr = Assert.IsType<StormReferenceExpr>(arm.Expression);
        Assert.Equal("RuleA", armExpr.Name);
        Assert.Equal("AltA", arm.AlternativeName);
        Assert.Null(arm.Label);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Valid inline block with 2 arms
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Parse_InlineBlock_TwoArms()
    {
        var source = "rule Foo = inline Prefix: { RuleA as AltA, RuleB as AltB };";
        var block = ParseInlineBlock(source, out var diags);

        Assert.Empty(diags);
        Assert.Equal(2, block.Arms.Count);

        var arm0 = block.Arms[0];
        var arm0Expr = Assert.IsType<StormReferenceExpr>(arm0.Expression);
        Assert.Equal("RuleA", arm0Expr.Name);
        Assert.Equal("AltA", arm0.AlternativeName);
        Assert.Null(arm0.Label);

        var arm1 = block.Arms[1];
        var arm1Expr = Assert.IsType<StormReferenceExpr>(arm1.Expression);
        Assert.Equal("RuleB", arm1Expr.Name);
        Assert.Equal("AltB", arm1.AlternativeName);
        Assert.Null(arm1.Label);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Trailing comma
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Parse_InlineBlock_TrailingComma()
    {
        var source = "rule Foo = inline Prefix: { RuleA as AltA, RuleB as AltB, };";
        var block = ParseInlineBlock(source, out var diags);

        Assert.Empty(diags);
        Assert.Equal(2, block.Arms.Count);
        Assert.Equal("AltA", block.Arms[0].AlternativeName);
        Assert.Equal("AltB", block.Arms[1].AlternativeName);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Labeled arms
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Parse_InlineBlock_LabeledArm()
    {
        var source = "rule Foo = inline Prefix: { myLabel:RuleA as AltA };";
        var block = ParseInlineBlock(source, out var diags);

        Assert.Empty(diags);
        _ = Assert.Single(block.Arms);

        var arm = block.Arms[0];
        Assert.Equal("myLabel", arm.Label);
        var armExpr = Assert.IsType<StormReferenceExpr>(arm.Expression);
        Assert.Equal("RuleA", armExpr.Name);
        Assert.Equal("AltA", arm.AlternativeName);
    }

    [Fact]
    public void Parse_InlineBlock_MultipleLabeledArms()
    {
        var source = "rule Foo = inline Prefix: { arm1:RuleA as AltA, arm2:RuleB as AltB };";
        var block = ParseInlineBlock(source, out var diags);

        Assert.Empty(diags);
        Assert.Equal(2, block.Arms.Count);

        Assert.Equal("arm1", block.Arms[0].Label);
        var expr0 = Assert.IsType<StormReferenceExpr>(block.Arms[0].Expression);
        Assert.Equal("RuleA", expr0.Name);
        Assert.Equal("AltA", block.Arms[0].AlternativeName);

        Assert.Equal("arm2", block.Arms[1].Label);
        var expr1 = Assert.IsType<StormReferenceExpr>(block.Arms[1].Expression);
        Assert.Equal("RuleB", expr1.Name);
        Assert.Equal("AltB", block.Arms[1].AlternativeName);
    }

    [Fact]
    public void Parse_InlineBlock_MixedLabeledAndUnlabeled()
    {
        var source = "rule Foo = inline Prefix: { myLabel:RuleA as AltA, RuleB as AltB };";
        var block = ParseInlineBlock(source, out var diags);

        Assert.Empty(diags);
        Assert.Equal(2, block.Arms.Count);

        Assert.Equal("myLabel", block.Arms[0].Label);
        Assert.Null(block.Arms[1].Label);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Nested inline blocks
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Parse_InlineBlock_NestedInlineBlock()
    {
        var source = @"rule Foo = inline OuterPrefix: {
            inner:inline InnerPrefix: { RuleA as InnerAltA, RuleB as InnerAltB } as OuterAlt1,
            RuleC as OuterAlt2,
        };";
        var block = ParseInlineBlock(source, out var diags);

        Assert.Empty(diags);
        var prefix = Assert.IsType<StormReferenceExpr>(block.Prefix);
        Assert.Equal("OuterPrefix", prefix.Name);
        Assert.Equal(2, block.Arms.Count);

        // First arm is a nested inline block
        var outerArm0 = block.Arms[0];
        Assert.Equal("inner", outerArm0.Label);
        Assert.Equal("OuterAlt1", outerArm0.AlternativeName);
        var nested = Assert.IsType<StormInlineBlockExpr>(outerArm0.Expression);

        var nestedPrefix = Assert.IsType<StormReferenceExpr>(nested.Prefix);
        Assert.Equal("InnerPrefix", nestedPrefix.Name);
        Assert.Equal(2, nested.Arms.Count);

        var innerArm0 = nested.Arms[0];
        var innerExpr0 = Assert.IsType<StormReferenceExpr>(innerArm0.Expression);
        Assert.Equal("RuleA", innerExpr0.Name);
        Assert.Equal("InnerAltA", innerArm0.AlternativeName);

        var innerArm1 = nested.Arms[1];
        var innerExpr1 = Assert.IsType<StormReferenceExpr>(innerArm1.Expression);
        Assert.Equal("RuleB", innerExpr1.Name);
        Assert.Equal("InnerAltB", innerArm1.AlternativeName);

        // Second arm is a plain rule reference
        var outerArm1 = block.Arms[1];
        Assert.Null(outerArm1.Label);
        Assert.Equal("OuterAlt2", outerArm1.AlternativeName);
        var outerExpr1 = Assert.IsType<StormReferenceExpr>(outerArm1.Expression);
        Assert.Equal("RuleC", outerExpr1.Name);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Source location
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Parse_InlineBlock_RecordsSourceLocation()
    {
        var source = "rule Foo = inline Prefix: { RuleA as AltA };";
        var block = ParseInlineBlock(source, out var diags);

        Assert.Empty(diags);
        // "inline" starts at offset 11 (after "rule Foo = ")
        Assert.Equal(11, block.Offset);
        Assert.Equal(1, block.Line);
        Assert.Equal(12, block.Column);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Inline block within alternation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Parse_InlineBlock_WithinAlternation()
    {
        var source = "rule Foo = A as First | inline Prefix: { RuleA as AltA, RuleB as AltB } | B as Last;";
        var file = Parse(source, out var diags);

        Assert.Empty(diags);
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        var alt = Assert.IsType<StormAlternationExpr>(decl.Body);
        Assert.Equal(3, alt.Alternatives.Count);

        // First alternative is named "First"
        var first = Assert.IsType<StormNamedAlternative>(alt.Alternatives[0]);
        Assert.Equal("First", first.AlternativeName);

        // Second alternative is the inline block (occupies one slot)
        var inlineBlock = Assert.IsType<StormInlineBlockExpr>(alt.Alternatives[1]);
        Assert.Equal(2, inlineBlock.Arms.Count);

        // Third alternative is named "Last"
        var last = Assert.IsType<StormNamedAlternative>(alt.Alternatives[2]);
        Assert.Equal("Last", last.AlternativeName);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Error recovery — empty braces
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Parse_InlineBlock_EmptyBraces_EmitsDiagnostic()
    {
        var source = "rule Foo = inline Prefix: { };";
        _ = ParseInlineBlock(source, out var diags);

        Assert.Contains(diags, d => d.Message.Contains("at least one arm"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Error recovery — missing `as`
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Parse_InlineBlock_MissingAs_EmitsDiagnostic()
    {
        var source = "rule Foo = inline Prefix: { RuleA AltA };";
        _ = Parse(source, out var diags);

        Assert.Contains(diags, d => d.Message.Contains("as"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Error recovery — missing identifier after `inline`
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Parse_InlineBlock_MissingIdentifierAfterInline_EmitsDiagnostic()
    {
        // Use a semicolon right after to avoid leaving a dangling brace
        var source = "rule Foo = inline ;";
        _ = Parse(source, out var diags);

        Assert.Contains(diags, d => d.Message.Contains("prefix rule name"));
    }

    [Fact]
    public void Parse_InlineBlock_MissingColonAfterPrefix_EmitsDiagnostic()
    {
        // Prefix followed by a semicolon instead of colon
        var source = "rule Foo = inline Prefix ;";
        _ = Parse(source, out var diags);

        Assert.Contains(diags, d => d.Message.Contains("':'"));
    }

    [Fact]
    public void Parse_InlineBlock_MissingBrace_EmitsDiagnostic()
    {
        // After the colon, directly hit a semicolon instead of a left brace
        var source = "rule Foo = inline Prefix: ;";
        _ = Parse(source, out var diags);

        Assert.Contains(diags, d => d.Message.Contains("'{'"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Error recovery — malformed arm continues parsing
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Parse_InlineBlock_MalformedArm_ContinuesParsing()
    {
        // First arm has identifier followed by identifier (missing as),
        // parser should recover at the comma and parse the second arm
        var source = "rule Foo = inline Prefix: { RuleA BadToken, RuleB as AltB };";
        var file = Parse(source, out var diags);

        // Should have a diagnostic for the bad arm
        Assert.NotEmpty(diags);
        // But should still parse the valid arm
        var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
        var block = Assert.IsType<StormInlineBlockExpr>(decl.Body);
        Assert.Contains(block.Arms, a => a.AlternativeName == "AltB");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Property 4: Inline Block Parsing Round-Trip
    //  Feature: inline-rule-prefix, Property 4: Inline Block Parsing Round-Trip
    //
    //  For any well-formed inline block source text (with valid prefix
    //  identifier, 1-N arms with valid rule references and as names,
    //  optional labels, optional trailing comma), parsing SHALL produce
    //  a StormInlineBlockExpr node whose Prefix is a StormReferenceExpr
    //  with the correct name, whose Arms list has the correct length,
    //  and whose each arm has the correct AlternativeName, Label, and Expression.
    //  **Validates: Requirements 2.1, 2.2, 2.3, 2.5**
    // ═══════════════════════════════════════════════════════════════════

    // Storm reserved keywords (see StormLexer.ClassifyKeyword). A generated
    // identifier whose lowercased form matches one of these is excluded: arm
    // labels and nested labels are lowercased via ToLowerInvariant(), so an
    // unfiltered identifier like "As" would become the keyword "as" and produce
    // invalid source (e.g. `{ as:RuleA as AltA }`), which fails to parse. Rule
    // references, alternative names, and prefixes keep their uppercase start and
    // are already keyword-safe, but filtering the shared generator uniformly is
    // harmless and mirrors the other Storm property tests.
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "rule", "token", "fragment", "hidden", "import", "grammar",
        "as", "mode", "default", "push", "pop", "inline", "trait",
    };

    private static Gen<string> GenIdentifier()
    {
        var startChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();
        var contChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

        return Gen.Elements(startChars).SelectMany(start =>
            Gen.Choose(1, 6).SelectMany(len =>
                Gen.ArrayOf(Gen.Elements(contChars), len)
                    .Select(chars => start + new string(chars))))
            .Where(id => !Keywords.Contains(id.ToLowerInvariant()));
    }

    private record InlineArmSpec(string? Label, string RuleRef, string AltName);

    private static Gen<InlineArmSpec> GenArm()
    {
        var labelGen = Gen.OneOf(
            Gen.Constant((string?)null),
            GenIdentifier().Select(s => (string?)s.ToLowerInvariant()));

        return labelGen.SelectMany(label =>
            GenIdentifier().SelectMany(ruleRef =>
                GenIdentifier().Select(altName =>
                    new InlineArmSpec(label, ruleRef, altName))));
    }

    private static Gen<bool> GenTrailingComma()
    {
        return Gen.Elements(true, false);
    }

    private static string BuildInlineBlockSource(string prefix, List<InlineArmSpec> arms, bool trailingComma)
    {
        var armStrings = arms.Select(a =>
        {
            var labelPart = a.Label != null ? $"{a.Label}:" : "";
            return $"{labelPart}{a.RuleRef} as {a.AltName}";
        });

        var separator = ", ";
        var armList = string.Join(separator, armStrings);
        if(trailingComma)
        {
            armList += ",";
        }

        return $"rule Test = inline {prefix}: {{ {armList} }};";
    }

    [Property(MaxTest = 100)]
    public Property P4_InlineBlockParsing_RoundTrip()
    {
        var testGen = GenIdentifier().SelectMany(prefix =>
            Gen.Choose(1, 5).SelectMany(armCount =>
                Gen.ListOf(GenArm(), armCount).SelectMany(arms =>
                    GenTrailingComma().Select(trailing =>
                        (prefix, arms: arms.ToList(), trailing)))));

        return Prop.ForAll(testGen.ToArbitrary(), drawn =>
        {
            var (prefix, arms, trailing) = drawn;
            var source = BuildInlineBlockSource(prefix, arms, trailing);
            var file = Parse(source, out var diags);

            Assert.Empty(diags);
            var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
            var block = Assert.IsType<StormInlineBlockExpr>(decl.Body);

            // Verify prefix
            var parsedPrefix = Assert.IsType<StormReferenceExpr>(block.Prefix);
            Assert.Equal(prefix, parsedPrefix.Name);

            // Verify arm count
            Assert.Equal(arms.Count, block.Arms.Count);

            // Verify each arm
            for(var i = 0; i < arms.Count; i++)
            {
                var expected = arms[i];
                var actual = block.Arms[i];

                Assert.Equal(expected.AltName, actual.AlternativeName);
                Assert.Equal(expected.Label, actual.Label);
                var actualExpr = Assert.IsType<StormReferenceExpr>(actual.Expression);
                Assert.Equal(expected.RuleRef, actualExpr.Name);
            }
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Property 5: Nested Inline Block Parsing
    //  Feature: inline-rule-prefix, Property 5: Nested Inline Block Parsing
    //
    //  For any inline block whose arms themselves contain nested inline
    //  blocks (up to depth 3), parsing SHALL produce correctly nested
    //  StormInlineBlockExpr nodes where each level's prefix and arms are
    //  independently correct.
    //  **Validates: Requirements 2.4**
    // ═══════════════════════════════════════════════════════════════════

    private static string BuildNestedInlineBlockSource(string outerPrefix, List<InlineArmSpec> outerPlainArms,
        string innerPrefix, List<InlineArmSpec> innerArms, string? nestedLabel, string nestedAltName)
    {
        // Build the inner inline block text
        var innerArmStrings = innerArms.Select(a =>
        {
            var labelPart = a.Label != null ? $"{a.Label}:" : "";
            return $"{labelPart}{a.RuleRef} as {a.AltName}";
        });
        var innerBlock = $"inline {innerPrefix}: {{ {string.Join(", ", innerArmStrings)} }}";

        // Build the nested arm (with optional label)
        var nestedLabelPart = nestedLabel != null ? $"{nestedLabel}:" : "";
        var nestedArmText = $"{nestedLabelPart}{innerBlock} as {nestedAltName}";

        // Build outer arms
        var outerArmStrings = outerPlainArms.Select(a =>
        {
            var labelPart = a.Label != null ? $"{a.Label}:" : "";
            return $"{labelPart}{a.RuleRef} as {a.AltName}";
        }).ToList();

        // Insert the nested arm at the beginning
        outerArmStrings.Insert(0, nestedArmText);

        return $"rule Test = inline {outerPrefix}: {{ {string.Join(", ", outerArmStrings)} }};";
    }

    [Property(MaxTest = 100)]
    public Property P5_NestedInlineBlockParsing()
    {
        var testGen = GenIdentifier().SelectMany(outerPrefix =>
            GenIdentifier().SelectMany(innerPrefix =>
                Gen.Choose(1, 3).SelectMany(innerArmCount =>
                    Gen.ListOf(GenArm(), innerArmCount).SelectMany(innerArms =>
                        Gen.Choose(0, 2).SelectMany(outerPlainCount =>
                            Gen.ListOf(GenArm(), outerPlainCount).SelectMany(outerPlainArms =>
                                Gen.OneOf(Gen.Constant((string?)null), GenIdentifier().Select(s => (string?)s.ToLowerInvariant())).SelectMany(nestedLabel =>
                                    GenIdentifier().Select(nestedAltName =>
                                        (outerPrefix, innerPrefix,
                                         innerArms: innerArms.ToList(),
                                         outerPlainArms: outerPlainArms.ToList(),
                                         nestedLabel, nestedAltName)))))))));

        return Prop.ForAll(testGen.ToArbitrary(), drawn =>
        {
            var (outerPrefix, innerPrefix, innerArms, outerPlainArms, nestedLabel, nestedAltName) = drawn;
            var source = BuildNestedInlineBlockSource(
                outerPrefix, outerPlainArms, innerPrefix, innerArms, nestedLabel, nestedAltName);
            var file = Parse(source, out var diags);

            Assert.Empty(diags);
            var decl = Assert.IsType<StormRuleDecl>(file.Declarations[0]);
            var block = Assert.IsType<StormInlineBlockExpr>(decl.Body);

            // Verify outer prefix
            var parsedOuterPrefix = Assert.IsType<StormReferenceExpr>(block.Prefix);
            Assert.Equal(outerPrefix, parsedOuterPrefix.Name);

            // Total arms = 1 nested + outerPlainArms
            Assert.Equal(1 + outerPlainArms.Count, block.Arms.Count);

            // Verify the nested arm
            var nestedArm = block.Arms[0];
            Assert.Equal(nestedAltName, nestedArm.AlternativeName);
            Assert.Equal(nestedLabel, nestedArm.Label);

            var nestedBlock = Assert.IsType<StormInlineBlockExpr>(nestedArm.Expression);
            var parsedInnerPrefix = Assert.IsType<StormReferenceExpr>(nestedBlock.Prefix);
            Assert.Equal(innerPrefix, parsedInnerPrefix.Name);
            Assert.Equal(innerArms.Count, nestedBlock.Arms.Count);

            // Verify inner arms
            for(var i = 0; i < innerArms.Count; i++)
            {
                var expected = innerArms[i];
                var actual = nestedBlock.Arms[i];

                Assert.Equal(expected.AltName, actual.AlternativeName);
                Assert.Equal(expected.Label, actual.Label);
                var actualExpr = Assert.IsType<StormReferenceExpr>(actual.Expression);
                Assert.Equal(expected.RuleRef, actualExpr.Name);
            }

            // Verify outer plain arms
            for(var i = 0; i < outerPlainArms.Count; i++)
            {
                var expected = outerPlainArms[i];
                var actual = block.Arms[i + 1]; // offset by 1 for the nested arm

                Assert.Equal(expected.AltName, actual.AlternativeName);
                Assert.Equal(expected.Label, actual.Label);
                var actualExpr = Assert.IsType<StormReferenceExpr>(actual.Expression);
                Assert.Equal(expected.RuleRef, actualExpr.Name);
            }
        });
    }
}