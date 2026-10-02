using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;
using Storm.SourceGenerator.Resolve;
using Storm.SourceGenerator.Validate;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Property-based tests for trait structural satisfaction.
/// Feature: storm-trait-keyword, Property 4: Structural satisfaction
///
/// **Validates: Requirement 5**
///
/// For any rule binding a trait where every trait field has a matching labeled expression
/// in the rule body with identical type name and cardinality, trait validation SHALL succeed
/// (no diagnostics). For any rule binding a trait where at least one trait field is missing
/// or mismatched, validation SHALL emit STORM0020 or STORM0021.
/// </summary>
public class StormTraitStructuralSatisfactionPropertyTests
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
    private record TraitFieldDef(string FieldName, string TypeName, StormFieldCardinality Cardinality);

    /// <summary>
    /// Generates a list of 1-5 trait fields with distinct field names and distinct type names.
    /// </summary>
    private static Gen<List<TraitFieldDef>> GenTraitFields()
    {
        return Gen.Choose(1, 5).SelectMany(count =>
            Gen.ArrayOf(
                GenFieldName().SelectMany(fn =>
                    GenTypeName().SelectMany(tn =>
                        GenCardinality().Select(c => new TraitFieldDef(fn, tn, c)))),
                count)
            .Select(fields =>
            {
                // Ensure unique field names and type names
                var seenFieldNames = new HashSet<string>();
                var seenTypeNames = new HashSet<string>();
                var result = new List<TraitFieldDef>();

                foreach(var f in fields)
                {
                    var fieldName = f.FieldName;
                    var typeName = f.TypeName;
                    var suffix = 0;

                    while(!seenFieldNames.Add(fieldName))
                    {
                        suffix++;
                        fieldName = f.FieldName + suffix;
                    }

                    suffix = 0;
                    while(!seenTypeNames.Add(typeName))
                    {
                        suffix++;
                        typeName = f.TypeName + suffix;
                    }

                    result.Add(new TraitFieldDef(fieldName, typeName, f.Cardinality));
                }

                return result;
            }));
    }

    // ══════════════════════════════════════════════════════════════════
    //  Helpers: Build Storm grammar source strings
    // ══════════════════════════════════════════════════════════════════

    private static bool DeclaresEachNameOnce(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return names.All(seen.Add);
    }

    private static void AssertGrammarBuilt(StormGrammar grammar, string source)
    {
        Assert.True(
            grammar.Succeeded,
            $"Grammar build failed for source:\n{source}\n" +
            $"Diagnostics: {string.Join("; ", grammar.Diagnostics.Select(d => $"{d.Id} {d.Message}"))}");
    }

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

    /// <summary>
    /// Wraps the inner expression with the appropriate cardinality operator for rule body fields.
    /// </summary>
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

    /// <summary>
    /// Builds token declarations for each type name referenced by traits.
    /// All types are declared as tokens with simple literal bodies.
    /// </summary>
    private static string BuildTokenDecls(IEnumerable<string> typeNames)
    {
        var index = 0;
        var lines = new List<string>();
        foreach(var name in typeNames)
        {
            lines.Add($"token {name} = \"{(char)('a' + index % 26)}{index}\";");
            index++;
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Builds a trait declaration from trait fields.
    /// </summary>
    private static string BuildTraitDecl(string traitName, List<TraitFieldDef> fields)
    {
        var fieldStrings = fields
            .Select(f => $"{f.FieldName}:{f.TypeName}{CardinalitySuffix(f.Cardinality)}");
        return $"trait {traitName} = {string.Join(" ", fieldStrings)};";
    }

    /// <summary>
    /// Builds a rule declaration that satisfies the given trait fields.
    /// Each trait field appears as a labeled expression with matching type and cardinality.
    /// </summary>
    private static string BuildSatisfyingRule(string ruleName, string traitName, List<TraitFieldDef> fields)
    {
        var bodyParts = fields
            .Select(f => $"{f.FieldName}:{WrapWithCardinality(f.TypeName, f.Cardinality)}");
        return $"rule {ruleName} : {traitName} = {string.Join(" ", bodyParts)};";
    }

    /// <summary>
    /// Builds a complete grammar source with tokens, a trait, and a satisfying rule.
    /// </summary>
    private static string BuildValidGrammarSource(string traitName, string ruleName, List<TraitFieldDef> fields)
    {
        var typeNames = fields.Select(f => f.TypeName).Distinct();
        var tokens = BuildTokenDecls(typeNames);
        var trait = BuildTraitDecl(traitName, fields);
        var rule = BuildSatisfyingRule(ruleName, traitName, fields);
        return $"{tokens}\n{trait}\n{rule}";
    }

    /// <summary>
    /// Builds a grammar where one field is missing from the rule body.
    /// </summary>
    private static string BuildMissingFieldGrammarSource(
        string traitName,
        string ruleName,
        List<TraitFieldDef> fields,
        int fieldToOmit)
    {
        var typeNames = fields.Select(f => f.TypeName).Distinct();
        var tokens = BuildTokenDecls(typeNames);
        var trait = BuildTraitDecl(traitName, fields);

        // Build rule body with one field omitted
        var bodyParts = fields
            .Where((_, i) => i != fieldToOmit)
            .Select(f => $"{f.FieldName}:{WrapWithCardinality(f.TypeName, f.Cardinality)}");

        // Need at least one expression in the body; use a dummy token ref if all fields removed
        var body = bodyParts.Any()
            ? string.Join(" ", bodyParts)
            : fields[0].TypeName;

        var rule = $"rule {ruleName} : {traitName} = {body};";
        return $"{tokens}\n{trait}\n{rule}";
    }

    /// <summary>
    /// Builds a grammar where one field has a mismatched type or cardinality.
    /// </summary>
    private static string BuildMismatchedFieldGrammarSource(
        string traitName,
        string ruleName,
        List<TraitFieldDef> fields,
        int fieldToMismatch,
        string wrongTypeName,
        StormFieldCardinality wrongCardinality)
    {
        var typeNames = fields.Select(f => f.TypeName).Append(wrongTypeName).Distinct();
        var tokens = BuildTokenDecls(typeNames);
        var trait = BuildTraitDecl(traitName, fields);

        // Build rule body with one field having wrong type/cardinality
        var bodyParts = fields.Select((f, i) =>
        {
            if(i == fieldToMismatch)
            {
                return $"{f.FieldName}:{WrapWithCardinality(wrongTypeName, wrongCardinality)}";
            }

            return $"{f.FieldName}:{WrapWithCardinality(f.TypeName, f.Cardinality)}";
        });

        var rule = $"rule {ruleName} : {traitName} = {string.Join(" ", bodyParts)};";
        return $"{tokens}\n{trait}\n{rule}";
    }

    // ══════════════════════════════════════════════════════════════════
    //  Property 4a: Valid satisfaction — no diagnostics
    //  For any trait with 1-5 fields, a rule body that matches each
    //  field (same name, same type reference, same cardinality) produces
    //  zero trait-related diagnostics.
    //  **Validates: Requirement 5**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 200)]
    public Property P4a_ValidSatisfaction_NoDiagnostics()
    {
        var traitNameGen = GenTypeName();
        var ruleNameGen = GenTypeName();
        var fieldsGen = GenTraitFields();

        var sampleGen = traitNameGen.SelectMany(tn =>
            ruleNameGen.SelectMany(rn =>
                fieldsGen.Select(fields => (TraitName: tn, RuleName: rn, Fields: fields))))
            .Where(x => DeclaresEachNameOnce(x.Fields.Select(f => f.TypeName).Append(x.TraitName).Append(x.RuleName)));

        return Prop.ForAll(sampleGen.ToArbitrary(), drawn =>
        {
            var (traitName, ruleName, fields) = drawn;
            var source = BuildValidGrammarSource(traitName, ruleName, fields);
            var grammar = ValidationTestHelpers.BuildGrammar(source);
            AssertGrammarBuilt(grammar, source);

            var diagnostics = TraitValidationPass.Run(grammar);

            var traitDiagnostics = diagnostics.Where(d =>
                d.Id == StormDiagnosticIds.STORM_TRAIT_FIELD_MISSING ||
                d.Id == StormDiagnosticIds.STORM_TRAIT_FIELD_MISMATCH).ToList();

            Assert.True(
                traitDiagnostics.Count == 0,
                $"Expected no trait satisfaction diagnostics for valid grammar but got:\n" +
                $"  Trait: {traitName}, Rule: {ruleName}\n" +
                $"  Fields: {string.Join(", ", fields.Select(f => $"{f.FieldName}:{f.TypeName}{CardinalitySuffix(f.Cardinality)}"))}\n" +
                $"  Diagnostics: {string.Join("; ", traitDiagnostics.Select(d => d.Message))}");
        });
    }

    // ══════════════════════════════════════════════════════════════════
    //  Property 4b: Missing field — STORM0020 emitted
    //  For any trait with 1-5 fields where one field is removed from
    //  the rule body, validation SHALL emit STORM0020.
    //  **Validates: Requirement 5**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 200)]
    public Property P4b_MissingField_EmitsSTORM0020()
    {
        var traitNameGen = GenTypeName();
        var ruleNameGen = GenTypeName();
        var fieldsGen = GenTraitFields();

        var sampleGen = traitNameGen.SelectMany(tn =>
            ruleNameGen.SelectMany(rn =>
                fieldsGen.SelectMany(fields =>
                    Gen.Choose(0, fields.Count - 1).Select(omit => (TraitName: tn, RuleName: rn, Fields: fields, FieldToOmit: omit)))))
            .Where(x => DeclaresEachNameOnce(x.Fields.Select(f => f.TypeName).Append(x.TraitName).Append(x.RuleName)));

        return Prop.ForAll(sampleGen.ToArbitrary(), drawn =>
        {
            var (traitName, ruleName, fields, fieldToOmit) = drawn;

            var source = BuildMissingFieldGrammarSource(traitName, ruleName, fields, fieldToOmit);
            var grammar = ValidationTestHelpers.BuildGrammar(source);
            AssertGrammarBuilt(grammar, source);

            var diagnostics = TraitValidationPass.Run(grammar);

            var missingFieldName = fields[fieldToOmit].FieldName;
            Assert.Contains(diagnostics, d =>
                d.Id == StormDiagnosticIds.STORM_TRAIT_FIELD_MISSING &&
                d.Message.Contains(missingFieldName) &&
                d.Message.Contains(traitName));
        });
    }

    // ══════════════════════════════════════════════════════════════════
    //  Property 4c: Mismatched field — STORM0021 emitted
    //  For any trait with 1-5 fields where one field has a different
    //  type or cardinality in the rule body, validation SHALL emit
    //  STORM0021.
    //  **Validates: Requirement 5**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 200)]
    public Property P4c_MismatchedField_EmitsSTORM0021()
    {
        var traitNameGen = GenTypeName();
        var ruleNameGen = GenTypeName();
        var fieldsGen = GenTraitFields();
        var wrongTypeGen = GenTypeName();
        var wrongCardGen = GenCardinality();

        var sampleGen = traitNameGen.SelectMany(tn =>
            ruleNameGen.SelectMany(rn =>
                fieldsGen.SelectMany(fields =>
                    Gen.Choose(0, fields.Count - 1).SelectMany(mismatch =>
                        wrongTypeGen.SelectMany(wt =>
                            wrongCardGen.Select(wc =>
                                (TraitName: tn, RuleName: rn, Fields: fields, FieldToMismatch: mismatch, WrongType: wt, WrongCard: wc)))))))
            .Where(x => DeclaresEachNameOnce(x.Fields.Select(f => f.TypeName).Append(x.TraitName).Append(x.RuleName).Append(x.WrongType)));

        return Prop.ForAll(sampleGen.ToArbitrary(), drawn =>
        {
            var (traitName, ruleName, fields, fieldToMismatch, wrongType, wrongCard) = drawn;
            var originalField = fields[fieldToMismatch];

            // Ensure we actually create a mismatch (different type OR different cardinality)
            var actualWrongType = wrongType;
            var actualWrongCard = wrongCard;

            if(actualWrongType == originalField.TypeName && actualWrongCard == originalField.Cardinality)
            {
                // Force a difference by changing cardinality
                actualWrongCard = originalField.Cardinality == StormFieldCardinality.One
                    ? StormFieldCardinality.Optional
                    : StormFieldCardinality.One;
            }

            var source = BuildMismatchedFieldGrammarSource(
                traitName, ruleName, fields, fieldToMismatch, actualWrongType, actualWrongCard);
            var grammar = ValidationTestHelpers.BuildGrammar(source);
            AssertGrammarBuilt(grammar, source);

            var diagnostics = TraitValidationPass.Run(grammar);

            var mismatchedFieldName = originalField.FieldName;
            Assert.Contains(diagnostics, d =>
                d.Id == StormDiagnosticIds.STORM_TRAIT_FIELD_MISMATCH &&
                d.Message.Contains(mismatchedFieldName) &&
                d.Message.Contains(traitName));
        });
    }
}