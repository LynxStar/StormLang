using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.Frontend;
using Storm.SourceGenerator.IR;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Property-based tests for trait interface emission correctness.
/// Feature: storm-trait-keyword, Property 5: Interface emission correctness
///
/// **Validates: Requirements 6, 7**
///
/// For any grammar with trait declarations and trait bindings, the emitted C# source SHALL
/// contain an interface named I{TraitName}Node for each trait, and each node class that binds
/// a trait SHALL include the trait interface in its implements list.
/// </summary>
public class StormTraitInterfaceEmissionPropertyTests
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
    /// Used for type names and trait names.
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
    /// Used for field names.
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
    /// Represents a trait field definition for test generation.
    /// </summary>
    private record TraitFieldDef(string FieldName, string TypeName, StormFieldCardinality Cardinality, bool IsToken);

    /// <summary>
    /// Represents a trait declaration for test generation.
    /// </summary>
    private record TraitDef(string Name, List<TraitFieldDef> Fields);

    /// <summary>
    /// Represents a rule that binds traits for test generation.
    /// </summary>
    private record RuleDef(string Name, List<string> TraitBindings, List<TraitFieldDef> AllFields);

    /// <summary>
    /// A complete generated scenario with tokens, traits, and rules.
    /// </summary>
    private record Scenario(
        List<string> TokenNames,
        List<TraitDef> Traits,
        List<RuleDef> Rules);

    /// <summary>
    /// Generates a complete test scenario with 1-3 tokens, 1-2 traits, and 1-2 rules binding those traits.
    /// </summary>
    private static Gen<Scenario> GenScenario()
    {
        return Gen.Choose(1, 3).SelectMany(tokenCount =>
            Gen.Choose(1, 2).SelectMany(traitCount =>
                Gen.Choose(1, 2).SelectMany(ruleCount =>
                    GenScenarioWithCounts(tokenCount, traitCount, ruleCount))));
    }

    private static Gen<Scenario> GenScenarioWithCounts(int tokenCount, int traitCount, int ruleCount)
    {
        // Generate unique token names
        return Gen.ArrayOf(GenTypeName(), tokenCount + 5).SelectMany(allTypeNames =>
            Gen.ArrayOf(GenFieldName(), traitCount * 4).SelectMany(allFieldNames =>
                Gen.ArrayOf(GenCardinality(), traitCount * 4).Select(allCards =>
                    BuildScenario(allTypeNames, allFieldNames, allCards, tokenCount, traitCount, ruleCount))));
    }

    private static Scenario BuildScenario(
        string[] allTypeNames,
        string[] allFieldNames,
        StormFieldCardinality[] allCards,
        int tokenCount,
        int traitCount,
        int ruleCount)
    {
        // Deduplicate type names
        var uniqueTypeNames = new List<string>();
        var seenTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var name in allTypeNames)
        {
            if(seenTypes.Add(name))
            {
                uniqueTypeNames.Add(name);
            }
        }

        // Ensure we have enough unique names
        while(uniqueTypeNames.Count < tokenCount + traitCount + ruleCount + 5)
        {
            var suffix = uniqueTypeNames.Count;
            var fallback = $"T{suffix}name";
            if(seenTypes.Add(fallback))
            {
                uniqueTypeNames.Add(fallback);
            }
        }

        // Assign token names from the unique list
        var tokenNames = uniqueTypeNames.Take(tokenCount).ToList();
        var remaining = uniqueTypeNames.Skip(tokenCount).ToList();

        // Assign trait names
        var traitNames = remaining.Take(traitCount).ToList();
        remaining = remaining.Skip(traitCount).ToList();

        // Assign rule names
        var ruleNames = remaining.Take(ruleCount).ToList();

        // Deduplicate field names
        var uniqueFieldNames = new List<string>();
        var seenFields = new HashSet<string>();
        foreach(var name in allFieldNames)
        {
            if(seenFields.Add(name))
            {
                uniqueFieldNames.Add(name);
            }
        }

        while(uniqueFieldNames.Count < traitCount * 4)
        {
            var suffix = uniqueFieldNames.Count;
            var fallback = $"f{suffix}ield";
            if(seenFields.Add(fallback))
            {
                uniqueFieldNames.Add(fallback);
            }
        }

        // Build traits: each trait gets 1-3 fields referencing tokens
        var traits = new List<TraitDef>();
        var fieldIndex = 0;
        var cardIndex = 0;
        foreach(var traitName in traitNames)
        {
            var fieldCount = Math.Min(1 + fieldIndex % 3, uniqueFieldNames.Count - fieldIndex);
            if(fieldCount < 1)
            {
                fieldCount = 1;
            }

            var fields = new List<TraitFieldDef>();
            for(var i = 0; i < fieldCount && fieldIndex < uniqueFieldNames.Count; i++)
            {
                var fieldName = uniqueFieldNames[fieldIndex];
                // All trait fields reference tokens for simplicity in this test
                var tokenRef = tokenNames[fieldIndex % tokenNames.Count];
                var card = allCards[cardIndex % allCards.Length];
                fields.Add(new TraitFieldDef(fieldName, tokenRef, card, IsToken: true));
                fieldIndex++;
                cardIndex++;
            }

            traits.Add(new TraitDef(traitName, fields));
        }

        // Build rules: each rule binds all traits and includes all trait fields in its body
        var rules = new List<RuleDef>();
        var allTraitFields = traits.SelectMany(t => t.Fields).ToList();
        foreach(var ruleName in ruleNames)
        {
            var bindings = traitNames.ToList();
            rules.Add(new RuleDef(ruleName, bindings, allTraitFields));
        }

        return new Scenario(tokenNames, traits, rules);
    }

    // ══════════════════════════════════════════════════════════════════
    //  Helpers: Build Storm grammar source and emit
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

    private static string WrapWithCardinality(string refExpr, StormFieldCardinality cardinality)
    {
        return cardinality switch
        {
            StormFieldCardinality.Optional => $"{refExpr}?",
            StormFieldCardinality.ZeroOrMore => $"{refExpr}*",
            StormFieldCardinality.OneOrMore => $"{refExpr}+",
            _ => refExpr
        };
    }

    private static string BuildGrammarSource(Scenario scenario)
    {
        var lines = new List<string>();

        // Token declarations
        var tokenIndex = 0;
        foreach(var tokenName in scenario.TokenNames)
        {
            lines.Add($"token {tokenName} = \"{(char)('a' + tokenIndex % 26)}{tokenIndex}\";");
            tokenIndex++;
        }

        // Trait declarations
        foreach(var trait in scenario.Traits)
        {
            var fieldStrings = trait.Fields
                .Select(f => $"{f.FieldName}:{f.TypeName}{CardinalitySuffix(f.Cardinality)}");
            lines.Add($"trait {trait.Name} = {string.Join(" ", fieldStrings)};");
        }

        // Rule declarations
        foreach(var rule in scenario.Rules)
        {
            var bindings = string.Join(", ", rule.TraitBindings);
            var bodyParts = rule.AllFields
                .Select(f => $"{f.FieldName}:{WrapWithCardinality(f.TypeName, f.Cardinality)}");
            lines.Add($"rule {rule.Name} : {bindings} = {string.Join(" ", bodyParts)};");
        }

        return string.Join("\n", lines);
    }

    private static string? EmitAstFromGrammar(string grammarSource)
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\n" + grammarSource
        };

        var resolver = new StormImportResolver(InMemoryFileReader(files));
        var resolution = resolver.Resolve("root/main.storm");
        var stormGrammar = StormGrammar.Build(resolution);
        var loweringResult = GrammarIRLowering.Lower(stormGrammar);

        if(!loweringResult.Succeeded || loweringResult.Grammar == null)
        {
            return null;
        }

        var traitResult = TraitIRBuilder.Build(stormGrammar);

        var emissionContext = new EmissionContext(
            "Test.Generated", "Test", loweringResult.Grammar,
            traits: traitResult.Traits,
            ruleTraitBindings: traitResult.RuleTraitBindings);

        return AstEmitter.EmitAstNodes(emissionContext);
    }

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

    /// <summary>
    /// Converts a camelCase field name to PascalCase (same logic as AstEmitter.PascalCase).
    /// </summary>
    private static string PascalCase(string name)
    {
        return string.IsNullOrEmpty(name) ? "Value" : char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// Computes the expected C# property type for a trait field, matching
    /// the cardinality-to-type mapping table in the design doc.
    /// </summary>
    private static string ExpectedPropertyType(TraitFieldDef field)
    {
        if(field.IsToken)
        {
            return field.Cardinality switch
            {
                StormFieldCardinality.Optional => "Token?",
                StormFieldCardinality.ZeroOrMore => "ImmutableArray<Token>",
                StormFieldCardinality.OneOrMore => "ImmutableArray<Token>",
                _ => "Token"
            };
        }

        var nodeType = field.TypeName + "Node";
        return field.Cardinality switch
        {
            StormFieldCardinality.Optional => $"{nodeType}?",
            StormFieldCardinality.ZeroOrMore => $"ImmutableArray<{nodeType}>",
            StormFieldCardinality.OneOrMore => $"ImmutableArray<{nodeType}>",
            _ => nodeType
        };
    }

    // ══════════════════════════════════════════════════════════════════
    //  Property 5a: Interface declaration existence
    //  For any grammar with trait declarations, the emitted C# SHALL
    //  contain `public interface I{TraitName}Node` for each trait.
    //  **Validates: Requirements 6, 7**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 200)]
    public Property P5a_InterfaceDeclaration_ExistsForEachTrait()
    {
        var scenarioGen = GenScenario();

        return Prop.ForAll(scenarioGen.ToArbitrary(), scenario =>
        {
            var source = BuildGrammarSource(scenario);
            var output = EmitAstFromGrammar(source);

            Assert.True(output != null,
                $"Emission failed for grammar:\n{source}");

            foreach(var trait in scenario.Traits)
            {
                var expectedInterface = $"public interface I{trait.Name}Node";
                Assert.True(output.Contains(expectedInterface),
                    $"Expected interface '{expectedInterface}' not found in emitted output.\n" +
                    $"Grammar source:\n{source}\n" +
                    $"Emitted output (first 500 chars):\n{output[..Math.Min(500, output.Length)]}");
            }
        });
    }

    // ══════════════════════════════════════════════════════════════════
    //  Property 5b: Interface properties have correct types
    //  For each trait interface, it SHALL contain a get-only property
    //  for each trait field with the correct C# type per the
    //  cardinality-to-type mapping table.
    //  **Validates: Requirements 6, 7**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 200)]
    public Property P5b_InterfaceProperties_HaveCorrectTypes()
    {
        var scenarioGen = GenScenario();

        return Prop.ForAll(scenarioGen.ToArbitrary(), scenario =>
        {
            var source = BuildGrammarSource(scenario);
            var output = EmitAstFromGrammar(source);

            Assert.True(output != null,
                $"Emission failed for grammar:\n{source}");

            foreach(var trait in scenario.Traits)
            {
                foreach(var field in trait.Fields)
                {
                    var expectedType = ExpectedPropertyType(field);
                    var expectedProp = $"{expectedType} {PascalCase(field.FieldName)} {{ get; }}";

                    Assert.True(output.Contains(expectedProp),
                        $"Expected property '{expectedProp}' not found in emitted interface I{trait.Name}Node.\n" +
                        $"Grammar source:\n{source}\n" +
                        $"Trait: {trait.Name}, Field: {field.FieldName}:{field.TypeName}{CardinalitySuffix(field.Cardinality)}");
                }
            }
        });
    }

    // ══════════════════════════════════════════════════════════════════
    //  Property 5c: Node class implements list includes trait interfaces
    //  For each rule that binds traits, the emitted sealed class SHALL
    //  include all corresponding I{TraitName}Node interfaces in its
    //  implements list.
    //  **Validates: Requirements 6, 7**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 200)]
    public Property P5c_NodeClass_IncludesTraitInterfacesInImplementsList()
    {
        var scenarioGen = GenScenario();

        return Prop.ForAll(scenarioGen.ToArbitrary(), scenario =>
        {
            var source = BuildGrammarSource(scenario);
            var output = EmitAstFromGrammar(source);

            Assert.True(output != null,
                $"Emission failed for grammar:\n{source}");

            foreach(var rule in scenario.Rules)
            {
                var nodeClassName = $"{rule.Name}Node";

                // Find the sealed class declaration line
                var classDecl = $"public sealed class {nodeClassName} :";
                var classIndex = output.IndexOf(classDecl);
                Assert.True(classIndex >= 0,
                    $"Expected sealed class '{nodeClassName}' not found in emitted output.\n" +
                    $"Grammar source:\n{source}");

                // Extract the line containing the class declaration (up to the opening brace)
                var lineEnd = output.IndexOf('\n', classIndex);
                var classLine = lineEnd >= 0
                    ? output[classIndex..lineEnd]
                    : output[classIndex..];

                foreach(var traitName in rule.TraitBindings)
                {
                    var expectedInterface = $"I{traitName}Node";
                    Assert.True(classLine.Contains(expectedInterface),
                        $"Expected interface '{expectedInterface}' in implements list of '{nodeClassName}'.\n" +
                        $"Actual class declaration: {classLine}\n" +
                        $"Grammar source:\n{source}");
                }
            }
        });
    }
}