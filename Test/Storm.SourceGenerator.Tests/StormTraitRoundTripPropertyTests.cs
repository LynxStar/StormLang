using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Property-based tests for trait declaration round-trip.
/// Feature: storm-trait-keyword, Property 1: Trait declaration round-trip
///
/// **Validates: Requirements 1, 2, 10**
///
/// For any valid Storm source containing trait declarations (with varying field counts 1..N,
/// varying cardinality suffixes, and optional trait composition), pretty-printing the parsed AST
/// and re-parsing the result SHALL produce a StormTraitDecl node whose trait name, field names,
/// field type names, field cardinalities, and composed trait names are identical to the original.
/// </summary>
public class StormTraitRoundTripPropertyTests
{
    // ══════════════════════════════════════════════════════════════════
    //  Generators
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Generates a type name (PascalCase-ish identifier that avoids keywords).
    /// </summary>
    private static Gen<string> GenTypeName()
    {
        var upperChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();
        var tailChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

        var startGen = Gen.Elements(upperChars);

        var tailGen = Gen.Choose(1, 8).SelectMany(len =>
            Gen.ArrayOf(Gen.Elements(tailChars), len)
                .Select(chars => new string(chars)));

        return startGen.SelectMany(start =>
            tailGen.Select(tail => start + tail));
    }

    /// <summary>
    /// Storm keywords that must be avoided in generated identifiers.
    /// </summary>
    private static readonly HashSet<string> StormKeywords = new(StringComparer.Ordinal)
    {
        "rule", "token", "fragment", "hidden", "import", "grammar",
        "as", "mode", "default", "push", "pop", "inline", "trait"
    };

    /// <summary>
    /// Generates a field name (camelCase-ish identifier that avoids keywords).
    /// </summary>
    private static Gen<string> GenFieldName()
    {
        var lowerChars = "abcdefghijklmnopqrstuvwxyz".ToCharArray();
        var tailChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

        var startGen = Gen.Elements(lowerChars);

        var tailGen = Gen.Choose(1, 8).SelectMany(len =>
            Gen.ArrayOf(Gen.Elements(tailChars), len)
                .Select(chars => new string(chars)));

        return startGen.SelectMany(start =>
            tailGen.Select(tail => start + tail))
            .Where(name => !StormKeywords.Contains(name));
    }

    /// <summary>
    /// Generates a random cardinality suffix.
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
    /// Generates a single trait field with unique field name, type name, and cardinality.
    /// </summary>
    private static Gen<(string FieldName, string TypeName, StormFieldCardinality Cardinality)> GenField()
    {
        return GenFieldName().SelectMany(fieldName =>
            GenTypeName().SelectMany(typeName =>
                GenCardinality().Select(card => (fieldName, typeName, card))));
    }

    /// <summary>
    /// Generates a list of 1..5 trait fields with distinct field names.
    /// </summary>
    private static Gen<List<(string FieldName, string TypeName, StormFieldCardinality Cardinality)>> GenFields()
    {
        return Gen.Choose(1, 5).SelectMany(count =>
            Gen.ArrayOf(GenField(), count)
                .Select(fields =>
                {
                    // Deduplicate field names by appending index
                    var seen = new HashSet<string>();
                    var result = new List<(string, string, StormFieldCardinality)>();
                    foreach(var (name, typeName, card) in fields)
                    {
                        var finalName = name;
                        var suffix = 0;
                        while(!seen.Add(finalName))
                        {
                            suffix++;
                            finalName = name + suffix;
                        }

                        result.Add((finalName, typeName, card));
                    }

                    return result;
                }));
    }

    /// <summary>
    /// Generates an optional list of 0..3 composed trait names.
    /// </summary>
    private static Gen<List<string>> GenComposedTraits()
    {
        return Gen.Choose(0, 3).SelectMany(count =>
            count == 0
                ? Gen.Constant(new List<string>())
                : Gen.ArrayOf(GenTypeName(), count)
                    .Select(names =>
                    {
                        // Deduplicate composed trait names
                        var seen = new HashSet<string>();
                        var result = new List<string>();
                        foreach(var name in names)
                        {
                            var finalName = name;
                            var suffix = 0;
                            while(!seen.Add(finalName))
                            {
                                suffix++;
                                finalName = name + suffix;
                            }

                            result.Add(finalName);
                        }

                        return result;
                    }));
    }

    // ══════════════════════════════════════════════════════════════════
    //  Helper: Build source string from generated data
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

    private static string BuildTraitSource(
        string traitName,
        List<(string FieldName, string TypeName, StormFieldCardinality Cardinality)> fields,
        List<string> composedTraits)
    {
        var composition = composedTraits.Count > 0
            ? $" : {string.Join(", ", composedTraits)}"
            : "";

        var fieldStrings = fields
            .Select(f => $"{f.FieldName}:{f.TypeName}{CardinalitySuffix(f.Cardinality)}");

        return $"trait {traitName}{composition} = {string.Join(" ", fieldStrings)};";
    }

    // ══════════════════════════════════════════════════════════════════
    //  Helper: Parse Storm source to StormFile
    // ══════════════════════════════════════════════════════════════════

    private static StormFile Parse(string input, out List<StormDiagnostic> diagnostics)
    {
        diagnostics = new List<StormDiagnostic>();
        var source = new StormSourceText("test.storm", input);
        var tokens = StormLexer.Tokenize(source, diagnostics);
        return StormParser.Parse(tokens, source, diagnostics);
    }

    // ══════════════════════════════════════════════════════════════════
    //  Property 1: Trait declaration round-trip
    //  Pretty-printing a parsed trait decl and re-parsing produces an
    //  identical StormTraitDecl.
    //  **Validates: Requirements 1, 2, 10**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 200)]
    public Property P1_TraitDeclRoundTrip_PrettyPrintAndReparse_ProducesIdenticalNode()
    {
        var traitNameGen = GenTypeName();
        var fieldsGen = GenFields();
        var composedGen = GenComposedTraits();

        var sampleGen = traitNameGen.SelectMany(name =>
            fieldsGen.SelectMany(fields =>
                composedGen.Select(composed => (name, fields, composed))));

        return Prop.ForAll(sampleGen.ToArbitrary(), drawn =>
        {
            var (traitName, fields, composedTraits) = drawn;

            // Build source from generated data
            var source = BuildTraitSource(traitName, fields, composedTraits);

            // Parse the original source
            var originalFile = Parse(source, out var originalDiags);
            Assert.Empty(originalDiags);
            _ = Assert.Single(originalFile.Declarations);
            var originalDecl = Assert.IsType<StormTraitDecl>(originalFile.Declarations[0]);

            // Pretty-print the parsed AST
            var printed = StormPrettyPrinter.Print(originalFile);

            // Re-parse the pretty-printed output
            var roundTrippedFile = Parse(printed, out var roundTripDiags);
            Assert.Empty(roundTripDiags);
            _ = Assert.Single(roundTrippedFile.Declarations);
            var roundTrippedDecl = Assert.IsType<StormTraitDecl>(roundTrippedFile.Declarations[0]);

            // Assert structural equality
            Assert.Equal(originalDecl.Name, roundTrippedDecl.Name);
            Assert.Equal(originalDecl.Fields.Count, roundTrippedDecl.Fields.Count);

            for(var i = 0; i < originalDecl.Fields.Count; i++)
            {
                Assert.Equal(originalDecl.Fields[i].FieldName, roundTrippedDecl.Fields[i].FieldName);
                Assert.Equal(originalDecl.Fields[i].TypeName, roundTrippedDecl.Fields[i].TypeName);
                Assert.Equal(originalDecl.Fields[i].Cardinality, roundTrippedDecl.Fields[i].Cardinality);
            }

            Assert.Equal(originalDecl.ComposedTraits.Count, roundTrippedDecl.ComposedTraits.Count);

            for(var i = 0; i < originalDecl.ComposedTraits.Count; i++)
            {
                Assert.Equal(originalDecl.ComposedTraits[i], roundTrippedDecl.ComposedTraits[i]);
            }
        });
    }
}