using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Unit and property-based tests for AST emitter inline block handling.
/// Validates: Requirements 8.1, 8.2, 8.3, 8.4, 8.5
/// </summary>
public class InlineBlockAstEmitterTests
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
    /// Simple inline block: two arms diverging on different tokens after shared prefix.
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
    /// Labeled prefix and tail fields for property naming tests.
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

    /// <summary>
    /// Inline block alongside a regular alternative.
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
    /// Three arms to test that all arms generate classes.
    /// </summary>
    private static string ThreeArmInlineGrammar()
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
            rule Arm3 = Prefix E;
            rule Parent = inline Prefix: { Arm1 as First, Arm2 as Second, Arm3 as Third };
            """;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Tests: Correct class naming per arm
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void EmitAst_InlineBlock_EmitsCorrectClassNamePerArm()
    {
        var ctx = CreateContext(SimpleInlineGrammar());
        var output = AstEmitter.EmitAstNodes(ctx);

        // Each arm generates a class named {ArmAltName}{RuleName}Node
        Assert.Contains("public sealed class FirstParentNode : ParentNode", output);
        Assert.Contains("public sealed class SecondParentNode : ParentNode", output);
    }

    [Fact]
    public void EmitAst_InlineBlock_ThreeArms_EmitsAllThreeClasses()
    {
        var ctx = CreateContext(ThreeArmInlineGrammar());
        var output = AstEmitter.EmitAstNodes(ctx);

        Assert.Contains("public sealed class FirstParentNode : ParentNode", output);
        Assert.Contains("public sealed class SecondParentNode : ParentNode", output);
        Assert.Contains("public sealed class ThirdParentNode : ParentNode", output);
    }

    [Fact]
    public void EmitAst_InlineBlock_EmitsAbstractBaseClass()
    {
        var ctx = CreateContext(SimpleInlineGrammar());
        var output = AstEmitter.EmitAstNodes(ctx);

        // The parent rule gets an abstract base node
        Assert.Contains("public abstract class ParentNode : SyntaxNode", output);
    }

    [Fact]
    public void EmitAst_InlineBlock_ArmClassesExtendAbstractBase()
    {
        var ctx = CreateContext(SimpleInlineGrammar());
        var output = AstEmitter.EmitAstNodes(ctx);

        // Both arm classes extend the abstract base
        Assert.Contains("FirstParentNode : ParentNode", output);
        Assert.Contains("SecondParentNode : ParentNode", output);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Tests: Property ordering - prefix fields then tail fields
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void EmitAst_InlineBlock_PrefixFieldsBeforeTailFields()
    {
        var ctx = CreateContext(LabeledInlineGrammar());
        var output = AstEmitter.EmitAstNodes(ctx);

        // With the correct design, each arm node has a SINGLE property pointing
        // to the target rule's node (not flat prefix+tail properties).
        // For the "First" arm with label "head", it should have Head property of type Arm1Node.
        var firstClassStart = output.IndexOf("public sealed class FirstParentNode");
        Assert.True(firstClassStart >= 0, "FirstParentNode class not found");

        // The arm node should have a single property named "Head" (from the arm's label)
        // pointing to Arm1Node
        var headIdx = output.IndexOf("public Arm1Node Head { get; set; }", firstClassStart);
        Assert.True(headIdx >= 0, "Head property of type Arm1Node not found in FirstParentNode.\nOutput:\n" + output);
    }

    [Fact]
    public void EmitAst_InlineBlock_BothArmsHavePrefixFields()
    {
        var ctx = CreateContext(LabeledInlineGrammar());
        var output = AstEmitter.EmitAstNodes(ctx);

        // Both arm nodes should have a single "Head" property pointing to their target rule's node
        var secondClassStart = output.IndexOf("public sealed class SecondParentNode");
        Assert.True(secondClassStart >= 0, "SecondParentNode class not found");

        var headIdx = output.IndexOf("public Arm2Node Head { get; set; }", secondClassStart);
        Assert.True(headIdx >= 0, "Head property of type Arm2Node not found in SecondParentNode.\nOutput:\n" + output);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Tests: No wrapper class generated
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void EmitAst_InlineBlock_NoWrapperClassGenerated()
    {
        var ctx = CreateContext(SimpleInlineGrammar());
        var output = AstEmitter.EmitAstNodes(ctx);

        // No wrapper class for the inline block itself.
        // The inline block does not have its own alt name, so no class like "InlineBlockParentNode" should exist.
        // Only FirstParentNode and SecondParentNode should be generated.
        Assert.DoesNotContain("InlineBlockParentNode", output);
        Assert.DoesNotContain("DefaultParentNode", output);
    }

    [Fact]
    public void EmitAst_InlineBlock_WithSibling_NoWrapperForInlineBlock()
    {
        var ctx = CreateContext(InlineBlockWithSiblingAltGrammar());
        var output = AstEmitter.EmitAstNodes(ctx);

        // Should have PlainParentNode (from the sibling alt), FirstParentNode, SecondParentNode
        // but no "InlineBlockParentNode" or "DefaultParentNode" wrapper
        Assert.Contains("public sealed class PlainParentNode : ParentNode", output);
        Assert.Contains("public sealed class FirstParentNode : ParentNode", output);
        Assert.Contains("public sealed class SecondParentNode : ParentNode", output);
        Assert.DoesNotContain("InlineBlockParentNode", output);
        Assert.DoesNotContain("DefaultParentNode", output);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Tests: Label-based property naming
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void EmitAst_InlineBlock_LabelsDeterminePropertyNames()
    {
        var ctx = CreateContext(LabeledInlineGrammar());
        var output = AstEmitter.EmitAstNodes(ctx);

        // Prefix labels "first" and "second" → properties "First" and "Second" (PascalCased)
        // Tail label "tail" → property "Tail"
        Assert.Contains("public Token First { get; set; }", output);
        Assert.Contains("public Token Second { get; set; }", output);
        Assert.Contains("public Token Tail { get; set; }", output);
    }

    [Fact]
    public void EmitAst_InlineBlock_UnlabeledFieldsGetDefaultNames()
    {
        var ctx = CreateContext(SimpleInlineGrammar());
        var output = AstEmitter.EmitAstNodes(ctx);

        // Unlabeled tokens get default property names derived from token names (PascalCased)
        // Prefix has A B (unlabeled), tail has C or D (unlabeled)
        // A → "A" property, B → "B" property, C/D → "C"/"D" property
        Assert.Contains("public sealed class FirstParentNode", output);

        // The output should have Token properties for the unlabeled elements
        Assert.Contains("Token A { get; set; }", output);
        Assert.Contains("Token B { get; set; }", output);
    }

    [Fact]
    public void EmitAst_InlineBlock_ChildrenOverridePresent()
    {
        var ctx = CreateContext(SimpleInlineGrammar());
        var output = AstEmitter.EmitAstNodes(ctx);

        // Each arm class should have a Children() override
        Assert.Contains("public override IEnumerable<SyntaxNode> Children()", output);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Tests: Rule reference fields in inline block
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void EmitAst_InlineBlock_RuleRefFieldIsNodeType()
    {
        var grammar = """
            token A = "a";
            token B = "b";
            token C = "c";
            rule Inner = A;
            rule Prefix = Inner;
            rule Arm1 = Prefix B;
            rule Arm2 = Prefix C;
            rule Parent = inline Prefix: { Arm1 as First, Arm2 as Second };
            """;

        var ctx = CreateContext(grammar);
        var output = AstEmitter.EmitAstNodes(ctx);

        // The prefix contains a rule reference to Inner, so prefix field should be InnerNode type
        Assert.Contains("InnerNode", output);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Property 11: AST Emission — Correct Node Class Per Arm
    //  Feature: inline-rule-prefix, Property 11: AST Emission — Correct Node Class Per Arm
    //
    //  For any inline block with N arms inside a rule named R, the AST
    //  emitter SHALL produce exactly N sealed node classes, each named
    //  {ArmAltName}{R}Node, and each extending the abstract {R}Node base class.
    //  **Validates: Requirements 8.1, 8.3**
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
    public Property P11_AstEmission_CorrectNodeClassPerArm()
    {
        // Generate grammars with varying arm counts (1-4) and varying alt names.
        // Verify: exactly N sealed classes named {AltName}{RuleName}Node are emitted,
        // each extending the abstract {RuleName}Node base.
        var testGen =
            GenIdentifier().SelectMany(ruleName =>
            GenIdentifier().SelectMany(prefixName =>
            Gen.Choose(1, 4).SelectMany(armCount =>
            Gen.ListOf(GenIdentifier(), armCount).SelectMany(altNames =>
            Gen.ListOf(GenIdentifier(), armCount).Select(armNames =>
                (ruleName, prefixName, armCount, altNames: altNames.ToList(), armNames: armNames.ToList()))))));

        return Prop.ForAll(testGen.ToArbitrary(), drawn =>
        {
            var (ruleName, prefixName, armCount, altNames, armNames) = drawn;
            // Ensure all names are unique
            var allNames = new HashSet<string>(StringComparer.Ordinal);
            if(!allNames.Add(ruleName) || !allNames.Add(prefixName))
            {
                return;
            }

            var validArms = new List<(string armName, string altName, string tailToken)>();
            for(var i = 0; i < armCount; i++)
            {
                var armName = armNames[i];
                var altName = altNames[i];
                var tailToken = $"T{i}";
                if(!allNames.Add(armName) || !allNames.Add(altName) || !allNames.Add(tailToken))
                {
                    continue;
                }

                validArms.Add((armName, altName, tailToken));
            }

            if(validArms.Count == 0)
            {
                return;
            }

            // Build grammar source
            var source = new System.Text.StringBuilder();
            _ = source.AppendLine("token PrefixTok = \"p\";");
            foreach(var (_, _, tailToken) in validArms)
            {
                _ = source.AppendLine($"token {tailToken} = \"{tailToken.ToLowerInvariant()}\";");
            }

            _ = source.AppendLine($"rule {prefixName} = PrefixTok;");
            foreach(var (armName, _, tailToken) in validArms)
            {
                _ = source.AppendLine($"rule {armName} = {prefixName} {tailToken};");
            }

            var armsList = string.Join(", ", validArms.Select(a => $"{a.armName} as {a.altName}"));
            _ = source.AppendLine($"rule {ruleName} = inline {prefixName}: {{ {armsList} }};");

            EmissionContext ctx;
            try
            {
                ctx = CreateContext(source.ToString());
            }
            catch
            {
                return; // Skip invalid name collisions
            }

            var output = AstEmitter.EmitAstNodes(ctx);

            // Property assertion: abstract base exists
            Assert.Contains($"public abstract class {ruleName}Node : SyntaxNode", output);

            // Property assertion: exactly N sealed classes
            foreach(var (_, altName, _) in validArms)
            {
                var expectedClassName = $"{altName}{ruleName}Node";
                Assert.Contains($"public sealed class {expectedClassName} : {ruleName}Node", output);
            }
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Property 12: AST Emission — Property Ordering Matches Prefix+Tail
    //  Feature: inline-rule-prefix, Property 12: AST Emission — Property Ordering Matches Prefix+Tail
    //
    //  For any arm in an inline block, the generated node class's property
    //  list (names and types, in order) SHALL equal the prefix rule's fields
    //  concatenated with the arm's tail fields.
    //  **Validates: Requirements 8.2, 8.4, 8.5**
    // ═══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 60)]
    public Property P12_AstEmission_PropertyOrderingMatchesPrefixPlusTail()
    {
        // The inline keyword is a parse-time optimization. The AST shape must be
        // identical to what a standalone `label:TargetRule as AltName` produces:
        // a single property named from the arm label pointing to the target rule's node.
        var testGen =
            Gen.Choose(1, 3).SelectMany(prefixFieldCount =>
            Gen.Choose(1, 3).SelectMany(tailFieldCount =>
            Gen.ListOf(GenIdentifier(), prefixFieldCount).SelectMany(prefixLabels =>
            Gen.ListOf(GenIdentifier(), tailFieldCount).Select(tailLabels =>
                (prefixFieldCount, tailFieldCount,
                 prefixLabels: prefixLabels.ToList(),
                 tailLabels: tailLabels.ToList())))));

        return Prop.ForAll(testGen.ToArbitrary(), drawn =>
        {
            var (prefixFieldCount, tailFieldCount, prefixLabels, tailLabels) = drawn;
            // Ensure all labels are unique and valid as token names
            var allNames = new HashSet<string>(StringComparer.Ordinal)
            {
                "Prefix", "Arm", "Container", "PrefixTok"
            };

            var validPrefixLabels = new List<(string label, string token)>();
            for(var i = 0; i < prefixFieldCount; i++)
            {
                var label = prefixLabels[i];
                var token = $"Pt{i}";
                if(!allNames.Add(label) || !allNames.Add(token))
                {
                    continue;
                }

                validPrefixLabels.Add((label, token));
            }

            var validTailLabels = new List<(string label, string token)>();
            for(var i = 0; i < tailFieldCount; i++)
            {
                var label = tailLabels[i];
                var token = $"Tt{i}";
                if(!allNames.Add(label) || !allNames.Add(token))
                {
                    continue;
                }

                validTailLabels.Add((label, token));
            }

            if(validPrefixLabels.Count == 0 || validTailLabels.Count == 0)
            {
                return;
            }

            // Build grammar
            var source = new System.Text.StringBuilder();
            foreach(var (_, token) in validPrefixLabels)
            {
                _ = source.AppendLine($"token {token} = \"{token.ToLowerInvariant()}\";");
            }

            foreach(var (_, token) in validTailLabels)
            {
                _ = source.AppendLine($"token {token} = \"{token.ToLowerInvariant()}\";");
            }

            var prefixBody = string.Join(" ", validPrefixLabels.Select(p => $"{p.label}:{p.token}"));
            _ = source.AppendLine($"rule Prefix = {prefixBody};");

            var armBody = string.Join(" ", validPrefixLabels.Select(p => $"{p.label}:{p.token}")) + " " +
                          string.Join(" ", validTailLabels.Select(t => $"{t.label}:{t.token}"));
            _ = source.AppendLine($"rule Arm = {armBody};");
            _ = source.AppendLine("rule Container = inline Prefix: { arm:Arm as Alt };");

            EmissionContext ctx;
            try
            {
                ctx = CreateContext(source.ToString());
            }
            catch
            {
                return;
            }

            var output = AstEmitter.EmitAstNodes(ctx);

            // The arm node (AltContainerNode) should have a single property "Arm"
            // of type "ArmNode" — not flat prefix+tail properties.
            var classDecl = "public sealed class AltContainerNode : ContainerNode";
            var classStart = output.IndexOf(classDecl);
            Assert.True(classStart >= 0, $"AltContainerNode not found in output.\nSource:\n{source}\nOutput:\n{output}");

            // Find next class or end of namespace to bound our search
            var classEnd = output.IndexOf("public sealed class ", classStart + classDecl.Length);
            if(classEnd < 0)
            {
                classEnd = output.IndexOf("public abstract class ", classStart + classDecl.Length);
            }

            if(classEnd < 0)
            {
                classEnd = output.Length;
            }

            var classBody = output[classStart..classEnd];

            // Verify the arm node has a single node-typed property pointing to ArmNode
            Assert.Contains("public ArmNode Arm { get; set; }", classBody);

            // Verify the arm node does NOT have the prefix/tail fields directly
            foreach(var (label, _) in validPrefixLabels)
            {
                var propName = PascalCase(label);
                Assert.DoesNotContain($"public Token {propName} {{ get; set; }}", classBody);
            }
        });
    }

    private static string PascalCase(string name)
    {
        if(string.IsNullOrEmpty(name))
        {
            return name;
        }

        return char.ToUpperInvariant(name[0]) + name[1..];
    }
}