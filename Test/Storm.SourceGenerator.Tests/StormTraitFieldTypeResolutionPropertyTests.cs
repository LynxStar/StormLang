using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Resolve;
using Storm.SourceGenerator.Validate;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Property-based tests for trait field type resolution.
/// Feature: storm-trait-keyword, Property 3: Trait field type resolution
///
/// **Validates: Requirements 4, 5**
///
/// For any trait field type name that matches a declared token name, the validation pass SHALL
/// NOT emit STORM0018. For any trait field type name that matches a declared rule name, the
/// validation pass SHALL NOT emit STORM0018. For any trait field type name that does not match
/// any token, rule, or fragment, the validation pass SHALL emit STORM0018.
/// </summary>
public class StormTraitFieldTypeResolutionPropertyTests
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
    private static Gen<string> GenPascalCaseIdentifier()
    {
        var upperChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();
        var alphaNumChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

        var gen = Gen.Elements(upperChars).SelectMany(start =>
            Gen.Choose(2, 8).SelectMany(len =>
                Gen.ArrayOf(Gen.Elements(alphaNumChars), len)
                    .Select(tail => start + new string(tail))));

        return gen.Where(id => !Keywords.Contains(id.ToLowerInvariant()));
    }

    /// <summary>
    /// Generates a camelCase field name identifier that avoids Storm keywords.
    /// </summary>
    private static Gen<string> GenFieldName()
    {
        var lowerChars = "abcdefghijklmnopqrstuvwxyz".ToCharArray();
        var alphaNumChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

        var gen = Gen.Elements(lowerChars).SelectMany(start =>
            Gen.Choose(2, 8).SelectMany(len =>
                Gen.ArrayOf(Gen.Elements(alphaNumChars), len)
                    .Select(tail => start + new string(tail))));

        return gen.Where(id => !Keywords.Contains(id));
    }

    /// <summary>
    /// Represents a trait field whose type references a declared token.
    /// </summary>
    private record TokenFieldCase(string FieldName, string TokenName);

    /// <summary>
    /// Represents a trait field whose type references a declared rule.
    /// </summary>
    private record RuleFieldCase(string FieldName, string RuleName);

    /// <summary>
    /// Represents a trait field whose type references an undefined name.
    /// </summary>
    private record UndefinedFieldCase(string FieldName, string UndefinedName);

    /// <summary>
    /// Generates a complete test scenario: a set of token names, rule names,
    /// and a trait with fields referencing a mix of tokens, rules, and undefined names.
    /// </summary>
    private record TestScenario(
        string TraitName,
        List<string> TokenNames,
        List<string> RuleNames,
        List<TokenFieldCase> TokenFields,
        List<RuleFieldCase> RuleFields,
        List<UndefinedFieldCase> UndefinedFields);

    private static Gen<TestScenario> GenScenario()
    {
        return GenPascalCaseIdentifier().SelectMany(traitName =>
            Gen.Choose(1, 3).SelectMany(tokenCount =>
                Gen.Choose(1, 3).SelectMany(ruleCount =>
                    Gen.Choose(0, 2).SelectMany(tokenFieldCount =>
                        Gen.Choose(0, 2).SelectMany(ruleFieldCount =>
                            Gen.Choose(0, 2).SelectMany(undefinedFieldCount =>
                                GenScenarioCore(
                                    traitName,
                                    tokenCount,
                                    ruleCount,
                                    tokenFieldCount,
                                    ruleFieldCount,
                                    undefinedFieldCount)))))));
    }

    private static Gen<TestScenario> GenScenarioCore(
        string traitName,
        int tokenCount,
        int ruleCount,
        int tokenFieldCount,
        int ruleFieldCount,
        int undefinedFieldCount)
    {
        // Ensure at least one field total (trait requires >= 1 field)
        var totalFields = tokenFieldCount + ruleFieldCount + undefinedFieldCount;
        if(totalFields == 0)
        {
            tokenFieldCount = 1;
        }

        var totalFieldCount = tokenFieldCount + ruleFieldCount + undefinedFieldCount;

        return Gen.ListOf(GenPascalCaseIdentifier(), tokenCount).SelectMany(tokenNames =>
            Gen.ListOf(GenPascalCaseIdentifier(), ruleCount).SelectMany(ruleNames =>
                Gen.ListOf(GenFieldName(), totalFieldCount).SelectMany(fieldNames =>
                    Gen.ListOf(GenPascalCaseIdentifier(), undefinedFieldCount).Select(undefinedTypeNames =>
                    {
                        // Ensure token and rule names don't overlap with each other or the trait name
                        var allNames = new HashSet<string> { traitName };
                        var uniqueTokens = new List<string>();
                        foreach(var t in tokenNames)
                        {
                            var name = t;
                            var suffix = 0;
                            while(!allNames.Add(name))
                            {
                                suffix++;
                                name = t + suffix;
                            }

                            uniqueTokens.Add(name);
                        }

                        var uniqueRules = new List<string>();
                        foreach(var r in ruleNames)
                        {
                            var name = r;
                            var suffix = 0;
                            while(!allNames.Add(name))
                            {
                                suffix++;
                                name = r + suffix;
                            }

                            uniqueRules.Add(name);
                        }

                        // Deduplicate field names
                        var fieldNameSet = new HashSet<string>();
                        var uniqueFieldNames = new List<string>();
                        foreach(var fn in fieldNames)
                        {
                            var name = fn;
                            var suffix = 0;
                            while(!fieldNameSet.Add(name))
                            {
                                suffix++;
                                name = fn + suffix;
                            }

                            uniqueFieldNames.Add(name);
                        }

                        // Deduplicate undefined type names (must not match any known name)
                        var uniqueUndefinedTypes = new List<string>();
                        foreach(var utn in undefinedTypeNames)
                        {
                            var name = utn;
                            var suffix = 0;
                            while(allNames.Contains(name) ||
                                   uniqueUndefinedTypes.Contains(name))
                            {
                                suffix++;
                                name = utn + suffix;
                            }

                            uniqueUndefinedTypes.Add(name);
                        }

                        // Assign fields: first tokenFieldCount get token types,
                        // next ruleFieldCount get rule types, rest get undefined types
                        var tokenFields = new List<TokenFieldCase>();
                        var ruleFields = new List<RuleFieldCase>();
                        var undefinedFields = new List<UndefinedFieldCase>();

                        var fieldIdx = 0;
                        for(var i = 0; i < tokenFieldCount && uniqueTokens.Count > 0; i++)
                        {
                            tokenFields.Add(new TokenFieldCase(
                                uniqueFieldNames[fieldIdx++],
                                uniqueTokens[i % uniqueTokens.Count]));
                        }

                        for(var i = 0; i < ruleFieldCount && uniqueRules.Count > 0; i++)
                        {
                            ruleFields.Add(new RuleFieldCase(
                                uniqueFieldNames[fieldIdx++],
                                uniqueRules[i % uniqueRules.Count]));
                        }

                        for(var i = 0; i < undefinedFieldCount; i++)
                        {
                            undefinedFields.Add(new UndefinedFieldCase(
                                uniqueFieldNames[fieldIdx++],
                                uniqueUndefinedTypes[i]));
                        }

                        return new TestScenario(
                            traitName,
                            uniqueTokens,
                            uniqueRules,
                            tokenFields,
                            ruleFields,
                            undefinedFields);
                    }))));
    }

    // ══════════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a complete Storm grammar source from a test scenario.
    /// Declares tokens, rules, and a trait referencing them.
    /// </summary>
    private static string BuildGrammarSource(TestScenario scenario)
    {
        var lines = new List<string> { "grammar Test;" };

        // Declare tokens
        foreach(var tokenName in scenario.TokenNames)
        {
            lines.Add($"token {tokenName} = \"{tokenName.ToLowerInvariant()}\";");
        }

        // Declare rules (they need to reference a valid token)
        var firstToken = scenario.TokenNames[0];
        foreach(var ruleName in scenario.RuleNames)
        {
            lines.Add($"rule {ruleName} = {firstToken};");
        }

        // Declare the trait with all fields
        var fields = new List<string>();
        foreach(var tf in scenario.TokenFields)
        {
            fields.Add($"{tf.FieldName}:{tf.TokenName}");
        }

        foreach(var rf in scenario.RuleFields)
        {
            fields.Add($"{rf.FieldName}:{rf.RuleName}");
        }

        foreach(var uf in scenario.UndefinedFields)
        {
            fields.Add($"{uf.FieldName}:{uf.UndefinedName}");
        }

        if(fields.Count > 0)
        {
            lines.Add($"trait {scenario.TraitName} = {string.Join(" ", fields)};");
        }

        return string.Join("\n", lines);
    }

    private static StormGrammar BuildGrammar(string source)
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = source,
        };
        var resolver = new StormImportResolver(ValidationTestHelpers.InMemoryFileReader(files));
        var resolution = resolver.Resolve("root/main.storm");
        return StormGrammar.Build(resolution);
    }

    // ══════════════════════════════════════════════════════════════════
    //  Property 3: Trait field type resolution
    //
    //  For valid field type references (tokens/rules), the validation
    //  pass SHALL NOT emit STORM0018 for those fields. For undefined
    //  field type references, it SHALL emit STORM0018.
    //
    //  **Validates: Requirements 4, 5**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 150)]
    public Property P3_TokenFieldType_NoSTORM0018Emitted()
    {
        var scenarios = GenScenario()
            .Where(s => s.TokenFields.Count > 0 && s.UndefinedFields.Count == 0 && s.RuleFields.Count == 0);

        return Prop.ForAll(scenarios.ToArbitrary(), scenario =>
        {
            var source = BuildGrammarSource(scenario);
            var grammar = BuildGrammar(source);
            Assert.True(grammar.Succeeded, $"Grammar build failed for source:\n{source}");

            var diagnostics = TraitValidationPass.Run(grammar);
            var storm0018 = diagnostics.Where(d => d.Id == StormDiagnosticIds.STORM_TRAIT_FIELD_UNRESOLVED).ToList();

            Assert.True(
                storm0018.Count == 0,
                $"Expected no STORM0018 for token field references but got {storm0018.Count}.\n" +
                $"Source:\n{source}\n" +
                $"Diagnostics: {string.Join("; ", storm0018.Select(d => d.Message))}");
        });
    }

    [Property(MaxTest = 150)]
    public Property P3_RuleFieldType_NoSTORM0018Emitted()
    {
        var scenarios = GenScenario()
            .Where(s => s.RuleFields.Count > 0 && s.UndefinedFields.Count == 0 && s.TokenFields.Count == 0);

        return Prop.ForAll(scenarios.ToArbitrary(), scenario =>
        {
            var source = BuildGrammarSource(scenario);
            var grammar = BuildGrammar(source);
            Assert.True(grammar.Succeeded, $"Grammar build failed for source:\n{source}");

            var diagnostics = TraitValidationPass.Run(grammar);
            var storm0018 = diagnostics.Where(d => d.Id == StormDiagnosticIds.STORM_TRAIT_FIELD_UNRESOLVED).ToList();

            Assert.True(
                storm0018.Count == 0,
                $"Expected no STORM0018 for rule field references but got {storm0018.Count}.\n" +
                $"Source:\n{source}\n" +
                $"Diagnostics: {string.Join("; ", storm0018.Select(d => d.Message))}");
        });
    }

    [Property(MaxTest = 150)]
    public Property P3_UndefinedFieldType_STORM0018Emitted()
    {
        var scenarios = GenScenario()
            .Where(s => s.UndefinedFields.Count > 0);

        return Prop.ForAll(scenarios.ToArbitrary(), scenario =>
        {
            var source = BuildGrammarSource(scenario);
            var grammar = BuildGrammar(source);
            Assert.True(grammar.Succeeded, $"Grammar build failed for source:\n{source}");

            var diagnostics = TraitValidationPass.Run(grammar);
            var storm0018 = diagnostics.Where(d => d.Id == StormDiagnosticIds.STORM_TRAIT_FIELD_UNRESOLVED).ToList();

            // Should emit STORM0018 for each undefined field type
            Assert.True(
                storm0018.Count >= scenario.UndefinedFields.Count,
                $"Expected at least {scenario.UndefinedFields.Count} STORM0018 diagnostic(s) " +
                $"for undefined field types but got {storm0018.Count}.\n" +
                $"Source:\n{source}\n" +
                $"Undefined types: {string.Join(", ", scenario.UndefinedFields.Select(f => f.UndefinedName))}");

            // Each undefined type name should appear in at least one diagnostic message
            foreach(var undefinedField in scenario.UndefinedFields)
            {
                Assert.True(
                    storm0018.Any(d => d.Message.Contains(undefinedField.UndefinedName)),
                    $"Expected STORM0018 to mention undefined type '{undefinedField.UndefinedName}' " +
                    $"but it did not.\nSource:\n{source}\n" +
                    $"Diagnostics: {string.Join("; ", storm0018.Select(d => d.Message))}");
            }
        });
    }

    [Property(MaxTest = 100)]
    public Property P3_MixedFieldTypes_STORM0018OnlyForUndefined()
    {
        var scenarios = GenScenario()
            .Where(s => s.TokenFields.Count > 0 && s.RuleFields.Count > 0 && s.UndefinedFields.Count > 0);

        return Prop.ForAll(scenarios.ToArbitrary(), scenario =>
        {
            var source = BuildGrammarSource(scenario);
            var grammar = BuildGrammar(source);
            Assert.True(grammar.Succeeded, $"Grammar build failed for source:\n{source}");

            var diagnostics = TraitValidationPass.Run(grammar);
            var storm0018 = diagnostics.Where(d => d.Id == StormDiagnosticIds.STORM_TRAIT_FIELD_UNRESOLVED).ToList();

            // Should only emit STORM0018 for undefined references, not for valid token/rule refs
            Assert.True(
                storm0018.Count == scenario.UndefinedFields.Count,
                $"Expected exactly {scenario.UndefinedFields.Count} STORM0018 diagnostic(s) " +
                $"but got {storm0018.Count}.\n" +
                $"Source:\n{source}\n" +
                $"Token fields: {string.Join(", ", scenario.TokenFields.Select(f => f.TokenName))}\n" +
                $"Rule fields: {string.Join(", ", scenario.RuleFields.Select(f => f.RuleName))}\n" +
                $"Undefined fields: {string.Join(", ", scenario.UndefinedFields.Select(f => f.UndefinedName))}\n" +
                $"Diagnostics: {string.Join("; ", storm0018.Select(d => d.Message))}");

            // Each STORM0018 should mention only undefined names, not valid token/rule names
            var validNames = scenario.TokenNames.Concat(scenario.RuleNames).ToHashSet();
            foreach(var diag in storm0018)
            {
                var mentionsValid = validNames.Any(name =>
                    diag.Message.Contains($"'{name}'") &&
                    diag.Message.Contains($"type '{name}'"));
                Assert.False(
                    mentionsValid,
                    $"STORM0018 should not mention valid type names.\n" +
                    $"Diagnostic: {diag.Message}");
            }
        });
    }

    private const string FixedTokenName = "Tok";

    [Property(MaxTest = 100)]
    public Property P3_FragmentFieldType_STORM0018Emitted()
    {
        // Fragments are not valid trait field types - they should also trigger STORM0018
        var cases = GenPascalCaseIdentifier().SelectMany(traitName =>
            GenPascalCaseIdentifier().SelectMany(fragmentName =>
                GenFieldName().Select(fieldName => (traitName, fragmentName, fieldName))))
            .Where(t => t.traitName != t.fragmentName && t.traitName != FixedTokenName && t.fragmentName != FixedTokenName);

        return Prop.ForAll(cases.ToArbitrary(), drawn =>
        {
            var (traitName, fragmentName, fieldName) = drawn;
            var source = $"grammar Test;\ntoken {FixedTokenName} = \"x\";\nfragment {fragmentName} = [a-z];\ntrait {traitName} = {fieldName}:{fragmentName};";
            var grammar = BuildGrammar(source);
            Assert.True(grammar.Succeeded, $"Grammar build failed for source:\n{source}");

            var diagnostics = TraitValidationPass.Run(grammar);
            var storm0018 = diagnostics.Where(d => d.Id == StormDiagnosticIds.STORM_TRAIT_FIELD_UNRESOLVED).ToList();

            Assert.True(
                storm0018.Count == 1,
                $"Expected exactly 1 STORM0018 for fragment field type but got {storm0018.Count}.\n" +
                $"Source:\n{source}\n" +
                $"Diagnostics: {string.Join("; ", storm0018.Select(d => d.Message))}");

            Assert.Contains(fragmentName, storm0018[0].Message);
        });
    }
}