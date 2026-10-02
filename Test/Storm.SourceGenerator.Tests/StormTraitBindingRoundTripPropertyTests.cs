using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Property-based tests for rule trait binding round-trip.
/// Feature: storm-trait-keyword, Property 2: Rule trait binding round-trip
/// </summary>
public class StormTraitBindingRoundTripPropertyTests
{
    // ══════════════════════════════════════════════════════════════════
    //  Generators
    // ══════════════════════════════════════════════════════════════════

    private static Gen<string> GenPascalCaseIdentifier()
    {
        var upperChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();
        var lowerChars = "abcdefghijklmnopqrstuvwxyz".ToCharArray();
        var alphaNumChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".ToCharArray();

        // Avoid keywords: trait, rule, token, fragment, grammar, import, mode, inline, as
        var keywords = new HashSet<string>
        {
            "trait", "rule", "token", "fragment", "grammar", "import", "mode", "inline", "as", "hidden", "push", "pop"
        };

        var gen = Gen.Elements(upperChars).SelectMany(start =>
            Gen.Choose(1, 10).SelectMany(len =>
                Gen.ArrayOf(Gen.Elements(alphaNumChars), len)
                    .Select(tail => start + new string(tail))));

        return gen.Where(id => !keywords.Contains(id.ToLowerInvariant()));
    }

    private static Gen<string> GenLowerCaseIdentifier()
    {
        var lowerChars = "abcdefghijklmnopqrstuvwxyz".ToCharArray();
        var alphaNumChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

        var keywords = new HashSet<string>
        {
            "trait", "rule", "token", "fragment", "grammar", "import", "mode", "inline", "as", "hidden", "push", "pop"
        };

        var gen = Gen.Elements(lowerChars).SelectMany(start =>
            Gen.Choose(1, 8).SelectMany(len =>
                Gen.ArrayOf(Gen.Elements(alphaNumChars), len)
                    .Select(tail => start + new string(tail))));

        return gen.Where(id => !keywords.Contains(id));
    }

    private static Gen<List<string>> GenTraitBindingList()
    {
        // Generate 1-3 unique trait names
        return Gen.Choose(1, 3).SelectMany(count =>
            Gen.ListOf(GenPascalCaseIdentifier(), count)
                .Select(names => names.Distinct().ToList()))
            .Where(list => list.Count > 0);
    }

    private static Gen<bool> GenIsMemo()
    {
        return Gen.Elements(true, false);
    }

    private record RuleWithTraitBindings(
        string RuleName,
        List<string> TraitBindings,
        string FieldName,
        string FieldTypeName,
        bool IsMemo);

    private static Gen<RuleWithTraitBindings> GenRuleWithTraitBindings()
    {
        return GenPascalCaseIdentifier().SelectMany(ruleName =>
            GenTraitBindingList().SelectMany(traits =>
                GenLowerCaseIdentifier().SelectMany(fieldName =>
                    GenPascalCaseIdentifier().SelectMany(fieldType =>
                        GenIsMemo().Select(isMemo =>
                            new RuleWithTraitBindings(ruleName, traits, fieldName, fieldType, isMemo))))));
    }

    // ══════════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════════

    private static StormFile Parse(string input, out List<StormDiagnostic> diagnostics)
    {
        diagnostics = [];
        var source = new StormSourceText("test.storm", input);
        var tokens = StormLexer.Tokenize(source, diagnostics);
        return StormParser.Parse(tokens, source, diagnostics);
    }

    private static string BuildSource(RuleWithTraitBindings rule)
    {
        var memo = rule.IsMemo ? "@memo " : "";
        var traitList = string.Join(", ", rule.TraitBindings);
        return $"{memo}rule {rule.RuleName} : {traitList} = {rule.FieldName}:{rule.FieldTypeName};";
    }

    // ══════════════════════════════════════════════════════════════════
    //  Property 2: Rule trait binding round-trip
    //  For any valid Storm source containing rule declarations with
    //  trait bindings, pretty-printing and re-parsing SHALL preserve
    //  the trait binding list in declaration order.
    //  **Validates: Requirements 3, 10**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 200)]
    public Property P2_RuleTraitBindingRoundTrip_PreservesTraitBindingListInOrder()
    {
        return Prop.ForAll(GenRuleWithTraitBindings().ToArbitrary(), rule =>
        {
            var source = BuildSource(rule);

            // Parse the original source
            var file1 = Parse(source, out var diags1);
            Assert.Empty(diags1);
            var decl1 = Assert.IsType<StormRuleDecl>(file1.Declarations[0]);
            Assert.NotNull(decl1.TraitBindings);
            Assert.Equal(rule.TraitBindings, decl1.TraitBindings.ToList());

            // Pretty-print
            var printed = StormPrettyPrinter.Print(file1);

            // Re-parse the pretty-printed output
            var file2 = Parse(printed, out var diags2);
            Assert.Empty(diags2);
            var decl2 = Assert.IsType<StormRuleDecl>(file2.Declarations[0]);
            Assert.NotNull(decl2.TraitBindings);

            // Assert trait binding list is preserved in declaration order
            Assert.Equal(decl1.TraitBindings.ToList(), decl2.TraitBindings.ToList());

            // Assert memo flag is preserved
            Assert.Equal(rule.IsMemo, decl2.IsMemo);

            // Assert rule name is preserved
            Assert.Equal(rule.RuleName, decl2.Name);
        });
    }
}