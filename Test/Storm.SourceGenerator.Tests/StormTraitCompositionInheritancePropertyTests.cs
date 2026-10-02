using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.Frontend;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;
using Storm.SourceGenerator.Validate;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Property-based tests for trait composition inheritance.
/// Feature: storm-trait-keyword, Property 6: Trait composition inheritance
///
/// **Validates: Requirements 3.6, 6.9, 7.3**
///
/// When a trait composes other traits (trait B : A = ...), the emitted IBNode interface
/// SHALL extend IANode. A rule binding B SHALL cause the emitted node to implement IBNode
/// (and transitively IANode through interface inheritance).
/// </summary>
public class StormTraitCompositionInheritancePropertyTests
{
    // ══════════════════════════════════════════════════════════════════
    //  Generators
    // ══════════════════════════════════════════════════════════════════

    private static readonly HashSet<string> Keywords = new()
    {
        "trait", "rule", "token", "fragment", "grammar", "import",
        "mode", "inline", "as", "hidden", "push", "pop"
    };

    /// <summary>
    /// Generates a PascalCase identifier that avoids Storm keywords.
    /// </summary>
    private static Gen<string> GenTypeName()
    {
        var upperChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();
        var tailChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

        return Gen.Elements(upperChars).SelectMany(start =>
            Gen.Choose(2, 8).SelectMany(len =>
                Gen.ArrayOf(Gen.Elements(tailChars), len)
                    .Select(chars => start + new string(chars))))
            .Where(id => !Keywords.Contains(id.ToLowerInvariant()));
    }

    /// <summary>
    /// Generates a camelCase identifier that avoids Storm keywords.
    /// </summary>
    private static Gen<string> GenFieldName()
    {
        var lowerChars = "abcdefghijklmnopqrstuvwxyz".ToCharArray();
        var tailChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

        return Gen.Elements(lowerChars).SelectMany(start =>
            Gen.Choose(2, 8).SelectMany(len =>
                Gen.ArrayOf(Gen.Elements(tailChars), len)
                    .Select(chars => start + new string(chars))))
            .Where(id => !Keywords.Contains(id));
    }

    /// <summary>
    /// Generates a random cardinality.
    /// </summary>
    private static Gen<StormFieldCardinality> GenCardinality()
    {
        return Gen.Elements(
            StormFieldCardinality.One,
            StormFieldCardinality.Optional,
            StormFieldCardinality.ZeroOrMore,
            StormFieldCardinality.OneOrMore);
    }

    /// <summary>
    /// A field definition: name, token type name, and cardinality.
    /// </summary>
    private record FieldDef(string FieldName, string TokenName, StormFieldCardinality Cardinality);

    /// <summary>
    /// The test scenario: a base trait A with fields, a composing trait B : A with additional
    /// fields, and a rule that binds B and satisfies all fields.
    /// </summary>
    private record CompositionScenario(
        string TraitAName,
        List<FieldDef> TraitAFields,
        string TraitBName,
        List<FieldDef> TraitBFields,
        string RuleName);

    /// <summary>
    /// Generates a composition scenario with guaranteed unique names.
    /// </summary>
    private static Gen<CompositionScenario> GenScenario()
    {
        return GenTypeName().SelectMany(traitAName =>
            GenTypeName().SelectMany(traitBName =>
                GenTypeName().SelectMany(ruleName =>
                    Gen.Choose(1, 3).SelectMany(aFieldCount =>
                        Gen.Choose(1, 3).SelectMany(bFieldCount =>
                            GenFieldDefs(aFieldCount).SelectMany(aFields =>
                                GenFieldDefs(bFieldCount).Select(bFields =>
                                {
                                    // Ensure all names are unique
                                    var allNames = new HashSet<string>();
                                    var finalTraitAName = MakeUnique(traitAName, allNames);
                                    var finalTraitBName = MakeUnique(traitBName, allNames);
                                    var finalRuleName = MakeUnique(ruleName, allNames);

                                    // Ensure field names and token names are unique across both traits
                                    var tokenNames = new HashSet<string>(allNames);
                                    var fieldNames = new HashSet<string>();
                                    var finalAFields = DeduplicateFields(aFields, fieldNames, tokenNames);
                                    var finalBFields = DeduplicateFields(bFields, fieldNames, tokenNames);

                                    return new CompositionScenario(
                                        finalTraitAName,
                                        finalAFields,
                                        finalTraitBName,
                                        finalBFields,
                                        finalRuleName);
                                })))))));
    }

    private static Gen<List<FieldDef>> GenFieldDefs(int count)
    {
        return Gen.ArrayOf(
            GenFieldName().SelectMany(fn =>
                GenTypeName().SelectMany(tn =>
                    GenCardinality().Select(c => new FieldDef(fn, tn, c)))),
            count)
            .Select(arr => arr.ToList());
    }

    private static string MakeUnique(string name, HashSet<string> existing)
    {
        var result = name;
        var suffix = 0;
        while(!existing.Add(result))
        {
            suffix++;
            result = name + suffix;
        }

        return result;
    }

    private static List<FieldDef> DeduplicateFields(List<FieldDef> fields, HashSet<string> fieldNames, HashSet<string> allNames)
    {
        var result = new List<FieldDef>();
        foreach(var f in fields)
        {
            var fieldName = f.FieldName;
            var suffix = 0;
            while(!fieldNames.Add(fieldName))
            {
                suffix++;
                fieldName = f.FieldName + suffix;
            }

            var tokenName = f.TokenName;
            suffix = 0;
            while(!allNames.Add(tokenName))
            {
                suffix++;
                tokenName = f.TokenName + suffix;
            }

            result.Add(new FieldDef(fieldName, tokenName, f.Cardinality));
        }

        return result;
    }

    // ══════════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════════

    private static string CardinalitySuffix(StormFieldCardinality cardinality)
    {
        return cardinality switch
        {
            StormFieldCardinality.Optional => "?",
            StormFieldCardinality.ZeroOrMore => "*",
            StormFieldCardinality.OneOrMore => "+",
            _ => ""
        };
    }

    private static string CardinalityRuleExpr(string tokenName, StormFieldCardinality cardinality)
    {
        return cardinality switch
        {
            StormFieldCardinality.Optional => $"{tokenName}?",
            StormFieldCardinality.ZeroOrMore => $"{tokenName}*",
            StormFieldCardinality.OneOrMore => $"{tokenName}+",
            _ => tokenName
        };
    }

    /// <summary>
    /// Builds a complete Storm grammar source for the composition scenario.
    /// </summary>
    private static string BuildGrammarSource(CompositionScenario scenario)
    {
        var lines = new List<string> { "grammar Test;" };

        // Declare all tokens (one per field across both traits)
        var allFields = scenario.TraitAFields.Concat(scenario.TraitBFields).ToList();
        foreach(var field in allFields)
        {
            lines.Add($"token {field.TokenName} = \"{field.TokenName.ToLowerInvariant()}\";");
        }

        // Declare trait A
        var aFieldStrings = scenario.TraitAFields
            .Select(f => $"{f.FieldName}:{f.TokenName}{CardinalitySuffix(f.Cardinality)}");
        lines.Add($"trait {scenario.TraitAName} = {string.Join(" ", aFieldStrings)};");

        // Declare trait B : A with its own fields
        var bFieldStrings = scenario.TraitBFields
            .Select(f => $"{f.FieldName}:{f.TokenName}{CardinalitySuffix(f.Cardinality)}");
        lines.Add($"trait {scenario.TraitBName} : {scenario.TraitAName} = {string.Join(" ", bFieldStrings)};");

        // Declare a rule that binds B and provides all fields (A's + B's)
        var ruleFieldExprs = allFields
            .Select(f => $"{f.FieldName}:{CardinalityRuleExpr(f.TokenName, f.Cardinality)}");
        lines.Add($"rule {scenario.RuleName} : {scenario.TraitBName} = {string.Join(" ", ruleFieldExprs)};");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Builds a StormGrammar from source, validates traits, lowers to IR,
    /// builds trait IR, and constructs an EmissionContext.
    /// </summary>
    private static (EmissionContext Context, string Source) BuildEmissionContext(CompositionScenario scenario)
    {
        var source = BuildGrammarSource(scenario);

        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = source,
        };
        var resolver = new StormImportResolver(ValidationTestHelpers.InMemoryFileReader(files));
        var resolution = resolver.Resolve("root/main.storm");
        var grammar = StormGrammar.Build(resolution);
        AssertStepSucceeded("Grammar build", grammar.Succeeded, grammar.Diagnostics, source);

        // Validate traits
        var traitDiags = TraitValidationPass.Run(grammar);
        AssertStepSucceeded(
            "Trait validation", !traitDiags.Any(d => d.Severity == StormDiagnosticSeverity.Error), traitDiags, source);

        // Lower to GrammarIR
        var loweringResult = GrammarIRLowering.Lower(grammar);
        AssertStepSucceeded("Lowering", loweringResult.Succeeded, loweringResult.Diagnostics, source);

        // Build trait IR
        var traitBuildResult = TraitIRBuilder.Build(grammar);

        // Construct EmissionContext with trait data
        var ctx = new EmissionContext(
            "Test.Generated",
            "Test",
            loweringResult.Grammar!,
            traits: traitBuildResult.Traits,
            ruleTraitBindings: traitBuildResult.RuleTraitBindings);

        return (ctx, source);
    }

    private static void AssertStepSucceeded(
        string step, bool succeeded, IEnumerable<StormDiagnostic> diagnostics, string source)
    {
        Assert.True(
            succeeded,
            $"{step} failed for source:\n{source}\n" +
            $"Diagnostics: {string.Join("; ", diagnostics.Select(d => $"{d.Id} {d.Message}"))}");
    }

    // ══════════════════════════════════════════════════════════════════
    //  Property 6: Trait composition inheritance
    //
    //  For any trait B that composes trait A, the emitted interface
    //  IBNode SHALL extend IANode. For any rule binding B, the emitted
    //  node SHALL implement IBNode (and transitively IANode).
    //
    //  **Validates: Requirements 3.6, 6.9, 7.3**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 150)]
    public Property P6_ComposingTrait_EmittedInterfaceExtendsBase()
    {
        return Prop.ForAll(GenScenario().ToArbitrary(), scenario =>
        {
            var (ctx, source) = BuildEmissionContext(scenario);
            var output = AstEmitter.EmitAstNodes(ctx);

            // Verify IBNode : IANode (the composing trait interface extends the base)
            var expectedDecl = $"public interface I{scenario.TraitBName}Node : I{scenario.TraitAName}Node";
            Assert.True(
                output.Contains(expectedDecl),
                $"Expected emitted output to contain '{expectedDecl}' but it did not.\n" +
                $"Source:\n{source}\n" +
                $"Output (trait interfaces section):\n{ExtractInterfaceSection(output, scenario.TraitBName)}");
        });
    }

    [Property(MaxTest = 150)]
    public Property P6_RuleBindingComposingTrait_NodeImplementsComposingInterface()
    {
        return Prop.ForAll(GenScenario().ToArbitrary(), scenario =>
        {
            var (ctx, source) = BuildEmissionContext(scenario);
            var output = AstEmitter.EmitAstNodes(ctx);

            // The rule's node class should implement IBNode directly
            var expectedImplements = $"I{scenario.TraitBName}Node";
            var classDecl = $"public sealed class {scenario.RuleName}Node";

            // Find the class declaration line and verify it includes the trait interface
            var classLine = output.Split('\n')
                .FirstOrDefault(l => l.Contains(classDecl));

            Assert.True(
                classLine != null,
                $"Expected emitted output to contain class declaration '{classDecl}'.\n" +
                $"Source:\n{source}");

            Assert.True(
                classLine!.Contains(expectedImplements),
                $"Expected node class to implement '{expectedImplements}'.\n" +
                $"Class declaration: {classLine.Trim()}\n" +
                $"Source:\n{source}");
        });
    }

    [Property(MaxTest = 150)]
    public Property P6_RuleBindingComposingTrait_BaseInterfaceInheritedTransitively()
    {
        return Prop.ForAll(GenScenario().ToArbitrary(), scenario =>
        {
            var (ctx, source) = BuildEmissionContext(scenario);
            var output = AstEmitter.EmitAstNodes(ctx);

            // The node class should NOT explicitly list IANode -- it's inherited
            // transitively through IBNode : IANode. The implements list should
            // contain IBNode but not IANode.
            var classDecl = $"public sealed class {scenario.RuleName}Node";
            var classLine = output.Split('\n')
                .FirstOrDefault(l => l.Contains(classDecl));

            Assert.True(classLine != null,
                $"Expected class declaration '{classDecl}' in output.\nSource:\n{source}");

            // IBNode is listed
            Assert.True(
                classLine!.Contains($"I{scenario.TraitBName}Node"),
                $"Expected node class to implement I{scenario.TraitBName}Node.\n" +
                $"Class: {classLine.Trim()}\nSource:\n{source}");

            // IANode is NOT listed directly (transitive inheritance)
            Assert.False(
                classLine.Contains($"I{scenario.TraitAName}Node"),
                $"Expected node class NOT to list I{scenario.TraitAName}Node directly " +
                $"(it should be inherited transitively via I{scenario.TraitBName}Node).\n" +
                $"Class: {classLine.Trim()}\nSource:\n{source}");
        });
    }

    [Property(MaxTest = 100)]
    public Property P6_BaseTraitInterface_HasOwnProperties()
    {
        return Prop.ForAll(GenScenario().ToArbitrary(), scenario =>
        {
            var (ctx, source) = BuildEmissionContext(scenario);
            var output = AstEmitter.EmitAstNodes(ctx);

            // IANode interface should declare its own properties
            var interfaceSection = ExtractInterfaceSection(output, scenario.TraitAName);
            Assert.True(
                interfaceSection != null,
                $"Expected I{scenario.TraitAName}Node interface in output.\nSource:\n{source}");

            foreach(var field in scenario.TraitAFields)
            {
                var propName = char.ToUpper(field.FieldName[0]) + field.FieldName[1..];
                Assert.True(
                    interfaceSection!.Contains($"{propName} {{ get; }}"),
                    $"Expected I{scenario.TraitAName}Node to declare property '{propName}'.\n" +
                    $"Interface section:\n{interfaceSection}\nSource:\n{source}");
            }
        });
    }

    // ══════════════════════════════════════════════════════════════════
    //  Utility
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Extracts the interface declaration section for a given trait name from the emitted output.
    /// </summary>
    private static string? ExtractInterfaceSection(string output, string traitName)
    {
        var interfaceDecl = $"public interface I{traitName}Node";
        var startIdx = output.IndexOf(interfaceDecl, StringComparison.Ordinal);
        if(startIdx < 0)
        {
            return null;
        }

        // Find the closing brace of the interface block
        var braceCount = 0;
        var inBlock = false;
        var endIdx = startIdx;
        for(var i = startIdx; i < output.Length; i++)
        {
            if(output[i] == '{')
            {
                braceCount++;
                inBlock = true;
            }
            else if(output[i] == '}')
            {
                braceCount--;
                if(inBlock && braceCount == 0)
                {
                    endIdx = i + 1;
                    break;
                }
            }
        }

        return output[startIdx..endIdx];
    }
}