using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Validate;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Property-based tests for multi-alternative rejection.
/// Feature: storm-trait-keyword, Property 8: Multi-alternative rejection
///
/// **Validates: Requirement 3.5**
///
/// For any rule that has named alternatives (multi-alternative rule) and also declares
/// trait bindings, the validation pass SHALL emit STORM0022.
/// </summary>
public class StormTraitMultiAltRejectionPropertyTests
{
    // ══════════════════════════════════════════════════════════════════
    //  Generators
    // ══════════════════════════════════════════════════════════════════

    private static Gen<string> GenPascalCaseIdentifier()
    {
        var upperChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();
        var alphaNumChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

        var keywords = new HashSet<string>
        {
            "trait", "rule", "token", "fragment", "grammar", "import", "mode", "inline", "as", "hidden", "push", "pop"
        };

        return Gen.Elements(upperChars).SelectMany(start =>
            Gen.Choose(2, 8).SelectMany(len =>
                Gen.ArrayOf(Gen.Elements(alphaNumChars), len)
                    .Select(tail => start + new string(tail))))
            .Where(id => !keywords.Contains(id.ToLowerInvariant()));
    }

    private static Gen<string> GenLowerCaseIdentifier()
    {
        var lowerChars = "abcdefghijklmnopqrstuvwxyz".ToCharArray();
        var alphaNumChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

        var keywords = new HashSet<string>
        {
            "trait", "rule", "token", "fragment", "grammar", "import", "mode", "inline", "as", "hidden", "push", "pop"
        };

        return Gen.Elements(lowerChars).SelectMany(start =>
            Gen.Choose(2, 8).SelectMany(len =>
                Gen.ArrayOf(Gen.Elements(alphaNumChars), len)
                    .Select(tail => start + new string(tail))))
            .Where(id => !keywords.Contains(id));
    }

    /// <summary>
    /// Generates a list of 1-3 unique trait binding names.
    /// </summary>
    private static Gen<List<string>> GenTraitBindingNames()
    {
        return Gen.Choose(1, 3).SelectMany(count =>
            Gen.ListOf(GenPascalCaseIdentifier(), count)
                .Select(names => names.Distinct().ToList()))
            .Where(list => list.Count > 0);
    }

    /// <summary>
    /// Generates a list of 2-4 named alternatives (e.g., "A as First | B as Second").
    /// Returns tuples of (referenceName, alternativeName).
    /// </summary>
    private static Gen<List<(string RefName, string AltName)>> GenNamedAlternatives()
    {
        return Gen.Choose(2, 4).SelectMany(count =>
        {
            var altGen = GenPascalCaseIdentifier().SelectMany(refName =>
                GenPascalCaseIdentifier().Select(altName => (refName, altName)));

            return Gen.ListOf(altGen, count)
                .Select(alts =>
                {
                    // Ensure unique alternative names and reference names
                    var seenAltNames = new HashSet<string>();
                    var seenRefNames = new HashSet<string>();
                    var result = new List<(string, string)>();
                    var suffix = 0;

                    foreach(var (refName, altName) in alts)
                    {
                        var finalRef = refName;
                        while(!seenRefNames.Add(finalRef))
                        {
                            suffix++;
                            finalRef = refName + suffix;
                        }

                        var finalAlt = altName;
                        while(!seenAltNames.Add(finalAlt))
                        {
                            suffix++;
                            finalAlt = altName + suffix;
                        }

                        result.Add((finalRef, finalAlt));
                    }

                    return result;
                });
        });
    }

    /// <summary>
    /// Generates a cardinality suffix for a trait field.
    /// </summary>
    private static Gen<string> GenCardinalitySuffix()
    {
        return Gen.Elements("", "?", "*", "+");
    }

    /// <summary>
    /// Generates a trait with 1-3 fields. Returns the trait name and the field declarations.
    /// </summary>
    private static Gen<(string TraitName, List<(string FieldName, string TypeName, string Cardinality)> Fields)> GenTrait()
    {
        return GenPascalCaseIdentifier().SelectMany(traitName =>
            Gen.Choose(1, 3).SelectMany(fieldCount =>
            {
                var fieldGen = GenLowerCaseIdentifier().SelectMany(fieldName =>
                    GenPascalCaseIdentifier().SelectMany(typeName =>
                        GenCardinalitySuffix().Select(card => (fieldName, typeName, card))));

                return Gen.ListOf(fieldGen, fieldCount)
                    .Select(fields =>
                    {
                        // Deduplicate field names
                        var seen = new HashSet<string>();
                        var result = new List<(string, string, string)>();
                        var suffix = 0;

                        foreach(var (fn, tn, card) in fields)
                        {
                            var finalFn = fn;
                            while(!seen.Add(finalFn))
                            {
                                suffix++;
                                finalFn = fn + suffix;
                            }

                            result.Add((finalFn, tn, card));
                        }

                        return (traitName, result);
                    });
            }));
    }

    // ══════════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a complete grammar source string with the given trait and a multi-alternative rule
    /// that binds the trait.
    /// </summary>
    private static string BuildMultiAltGrammar(
        string traitName,
        List<(string FieldName, string TypeName, string Cardinality)> traitFields,
        string ruleName,
        List<string> traitBindings,
        List<(string RefName, string AltName)> alternatives)
    {
        // Build trait field declarations for the trait
        var fieldDecls = string.Join(" ", traitFields.Select(f => $"{f.FieldName}:{f.TypeName}{f.Cardinality}"));
        var traitDecl = $"trait {traitName} = {fieldDecls};";

        // Build token declarations for each reference name used in alternatives
        var tokenDecls = string.Join("\n", alternatives.Select(a => $"token {a.RefName} = /{a.RefName.ToLowerInvariant()}/;"));

        // Build the multi-alternative rule with trait bindings
        var traitBindingStr = string.Join(", ", traitBindings);
        var altBody = string.Join(" | ", alternatives.Select(a => $"{a.RefName} as {a.AltName}"));
        var ruleDecl = $"rule {ruleName} : {traitBindingStr} = {altBody};";

        return $"{traitDecl}\n{tokenDecls}\n{ruleDecl}";
    }

    /// <summary>
    /// Builds a complete grammar source string with a trait and a single-alternative rule
    /// that binds the trait and has matching fields.
    /// </summary>
    private static string BuildSingleAltGrammar(
        string traitName,
        List<(string FieldName, string TypeName, string Cardinality)> traitFields,
        string ruleName,
        List<string> traitBindings)
    {
        // Build trait field declarations
        var fieldDecls = string.Join(" ", traitFields.Select(f => $"{f.FieldName}:{f.TypeName}{f.Cardinality}"));
        var traitDecl = $"trait {traitName} = {fieldDecls};";

        // For each trait field, declare a token or rule that matches the type name
        var typeDecls = traitFields
            .Select(f => f.TypeName)
            .Distinct()
            .Select(tn => $"token {tn} = /{tn.ToLowerInvariant()}/;");
        var typeDeclStr = string.Join("\n", typeDecls);

        // Build the single-alternative rule body with labeled fields matching the trait
        var ruleBody = string.Join(" ", traitFields.Select(f =>
        {
            var cardSuffix = f.Cardinality switch
            {
                "?" => "?",
                "*" => "*",
                "+" => "+",
                _ => ""
            };
            return $"{f.FieldName}:{f.TypeName}{cardSuffix}";
        }));

        var traitBindingStr = string.Join(", ", traitBindings);
        var ruleDecl = $"rule {ruleName} : {traitBindingStr} = {ruleBody};";

        return $"{traitDecl}\n{typeDeclStr}\n{ruleDecl}";
    }

    // ══════════════════════════════════════════════════════════════════
    //  Property 8: Multi-alternative rejection
    //  For any rule with named alternatives that also declares trait
    //  bindings, the validation pass SHALL emit STORM0022.
    //  **Validates: Requirement 3.5**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 200)]
    public Property P8_MultiAltRuleWithTraitBindings_AlwaysEmitsSTORM0022()
    {
        var testCaseGen = GenTrait().SelectMany(trait =>
            GenPascalCaseIdentifier().SelectMany(ruleName =>
                GenTraitBindingNames().SelectMany(extraBindings =>
                    GenNamedAlternatives().Select(alts =>
                    {
                        // Ensure the trait name is in the binding list
                        var bindings = new List<string> { trait.TraitName };
                        foreach(var b in extraBindings.Where(b => b != trait.TraitName))
                        {
                            bindings.Add(b);
                        }

                        // Ensure rule name differs from trait name and token names
                        var finalRuleName = ruleName;
                        var reserved = new HashSet<string>(alts.Select(a => a.RefName))
                        {
                            trait.TraitName
                        };
                        var suffix = 0;
                        while(reserved.Contains(finalRuleName))
                        {
                            suffix++;
                            finalRuleName = ruleName + suffix;
                        }

                        return (trait.TraitName, trait.Fields, finalRuleName, bindings, alts);
                    }))))
            .Where(drawn => drawn.alts.All(a => a.RefName != drawn.TraitName));

        return Prop.ForAll(testCaseGen.ToArbitrary(), drawn =>
        {
            var (traitName, fields, ruleName, bindings, alts) = drawn;
            var source = BuildMultiAltGrammar(traitName, fields, ruleName, bindings, alts);
            var grammar = ValidationTestHelpers.BuildGrammar(source);
            Assert.True(grammar.Succeeded,
                $"Grammar build failed for source:\n{source}\n" +
                $"Diagnostics: {string.Join("; ", grammar.Diagnostics.Select(d => $"{d.Id} {d.Message}"))}");

            var diagnostics = TraitValidationPass.Run(grammar);

            Assert.Contains(diagnostics, d =>
                d.Id == StormDiagnosticIds.STORM_TRAIT_MULTI_ALT &&
                d.Message.Contains(ruleName));
        });
    }

    [Property(MaxTest = 200)]
    public Property P8_Inverse_SingleAltRuleWithTraitBindings_NeverEmitsSTORM0022()
    {
        var testCaseGen = GenTrait().SelectMany(trait =>
            GenPascalCaseIdentifier().Select(ruleName =>
            {
                // Ensure rule name differs from trait name and type names
                var reserved = new HashSet<string>(trait.Fields.Select(f => f.TypeName))
                {
                    trait.TraitName
                };
                var finalRuleName = ruleName;
                var suffix = 0;
                while(reserved.Contains(finalRuleName))
                {
                    suffix++;
                    finalRuleName = ruleName + suffix;
                }

                return (trait.TraitName, trait.Fields, finalRuleName);
            }))
            .Where(drawn => drawn.Fields.All(f => f.TypeName != drawn.TraitName));

        return Prop.ForAll(testCaseGen.ToArbitrary(), drawn =>
        {
            var (traitName, fields, ruleName) = drawn;
            var bindings = new List<string> { traitName };
            var source = BuildSingleAltGrammar(traitName, fields, ruleName, bindings);
            var grammar = ValidationTestHelpers.BuildGrammar(source);
            Assert.True(grammar.Succeeded,
                $"Grammar build failed for source:\n{source}\n" +
                $"Diagnostics: {string.Join("; ", grammar.Diagnostics.Select(d => $"{d.Id} {d.Message}"))}");

            var diagnostics = TraitValidationPass.Run(grammar);

            Assert.DoesNotContain(diagnostics, d =>
                d.Id == StormDiagnosticIds.STORM_TRAIT_MULTI_ALT);
        });
    }
}