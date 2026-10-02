using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Validate;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Unit and property-based tests for the nominal prefix verification pass.
/// Validates: Requirements 4.1, 4.2, 4.3, 4.4, 4.5, 4.6, 9.1, 9.2, 9.3, 9.5
/// </summary>
public class InlinePrefixVerificationTests
{
    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static List<StormDiagnostic> Verify(string source)
    {
        var grammar = ValidationTestHelpers.BuildGrammar(source);
        return InlinePrefixVerification.Verify(grammar);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Passing case — arm starts with prefix rule reference
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Verify_PassingCase_ArmStartsWithPrefixRule_NoDiagnostics()
    {
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            rule Prefix = A B;
            rule Arm1 = Prefix C;
            rule R = inline Prefix: { Arm1 as Alt1 };
            """;

        var diagnostics = Verify(source);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Verify_PassingCase_MultipleArmsAllStartWithPrefix_NoDiagnostics()
    {
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            token D = "d";
            rule Prefix = A B;
            rule Arm1 = Prefix C;
            rule Arm2 = Prefix D;
            rule R = inline Prefix: { Arm1 as Alt1, Arm2 as Alt2 };
            """;

        var diagnostics = Verify(source);

        Assert.Empty(diagnostics);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Failing case — arm starts with different reference
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Verify_FailingCase_ArmStartsWithDifferentRule_EmitsSTORM0017()
    {
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            rule Prefix = A B;
            rule OtherRule = A;
            rule Arm1 = OtherRule C;
            rule R = inline Prefix: { Arm1 as Alt1 };
            """;

        var diagnostics = Verify(source);

        _ = Assert.Single(diagnostics);
        Assert.Equal(StormDiagnosticIds.STORM_INLINE_PREFIX_MISMATCH, diagnostics[0].Id);
        Assert.Equal(StormDiagnosticSeverity.Error, diagnostics[0].Severity);
        Assert.Contains("Arm1", diagnostics[0].Message);
        Assert.Contains("Prefix", diagnostics[0].Message);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Labeled prefix reference — unwrap label before checking
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Verify_LabeledPrefixReference_PassesVerification()
    {
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            rule Prefix = A B;
            rule Arm1 = head:Prefix C;
            rule R = inline Prefix: { Arm1 as Alt1 };
            """;

        var diagnostics = Verify(source);

        Assert.Empty(diagnostics);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Unresolved prefix rule → error
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Verify_UnresolvedPrefixRule_EmitsError()
    {
        var source = """
            token A = "a";
            token C = "c";
            rule Arm1 = A C;
            rule R = inline NonExistent: { Arm1 as Alt1 };
            """;

        var diagnostics = Verify(source);

        _ = Assert.Single(diagnostics);
        Assert.Equal(StormDiagnosticIds.STORM_INLINE_PREFIX_MISMATCH, diagnostics[0].Id);
        Assert.Equal(StormDiagnosticSeverity.Error, diagnostics[0].Severity);
        Assert.Contains("NonExistent", diagnostics[0].Message);
        Assert.Contains("undefined", diagnostics[0].Message);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Prefix rule with alternation body → error
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Verify_PrefixRuleHasAlternation_EmitsError()
    {
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            rule Prefix = A | B;
            rule Arm1 = Prefix C;
            rule R = inline Prefix: { Arm1 as Alt1 };
            """;

        var diagnostics = Verify(source);

        _ = Assert.Single(diagnostics);
        Assert.Equal(StormDiagnosticIds.STORM_INLINE_PREFIX_MISMATCH, diagnostics[0].Id);
        Assert.Equal(StormDiagnosticSeverity.Error, diagnostics[0].Severity);
        Assert.Contains("Prefix", diagnostics[0].Message);
        Assert.Contains("alternation", diagnostics[0].Message);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Arm rule with alternation body → error
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Verify_ArmRuleHasAlternation_EmitsError()
    {
        var source = """
            token A = "a";
            token C = "c";
            token D = "d";
            rule Prefix = A;
            rule Arm1 = Prefix C | D;
            rule R = inline Prefix: { Arm1 as Alt1 };
            """;

        var diagnostics = Verify(source);

        _ = Assert.Single(diagnostics);
        Assert.Equal(StormDiagnosticIds.STORM_INLINE_PREFIX_MISMATCH, diagnostics[0].Id);
        Assert.Equal(StormDiagnosticSeverity.Error, diagnostics[0].Severity);
        Assert.Contains("Arm1", diagnostics[0].Message);
        Assert.Contains("alternation", diagnostics[0].Message);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Multiple failing arms → one diagnostic per arm
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Verify_MultipleFailingArms_OneDiagnosticPerArm()
    {
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            token D = "d";
            rule Prefix = A B;
            rule OtherRule = C;
            rule Arm1 = OtherRule C;
            rule Arm2 = OtherRule D;
            rule R = inline Prefix: { Arm1 as Alt1, Arm2 as Alt2 };
            """;

        var diagnostics = Verify(source);

        Assert.Equal(2, diagnostics.Count);
        Assert.All(diagnostics, d =>
            Assert.Equal(StormDiagnosticIds.STORM_INLINE_PREFIX_MISMATCH, d.Id));
        Assert.Contains(diagnostics, d => d.Message.Contains("Alt1"));
        Assert.Contains(diagnostics, d => d.Message.Contains("Alt2"));
    }

    [Fact]
    public void Verify_MixedPassingAndFailingArms_DiagnosticsOnlyForFailing()
    {
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            token D = "d";
            rule Prefix = A B;
            rule OtherRule = C;
            rule GoodArm = Prefix C;
            rule BadArm = OtherRule D;
            rule R = inline Prefix: { GoodArm as Alt1, BadArm as Alt2 };
            """;

        var diagnostics = Verify(source);

        _ = Assert.Single(diagnostics);
        Assert.Contains("Alt2", diagnostics[0].Message);
        Assert.Contains("BadArm", diagnostics[0].Message);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Property 6: Nominal Prefix Verification Correctness
    //  Feature: inline-rule-prefix, Property 6: Nominal Prefix Verification Correctness
    //
    //  For any grammar with inline blocks where every arm starts with the
    //  prefix rule by name → zero STORM0017. For any grammar where an arm
    //  doesn't start with the prefix rule → exactly one STORM0017 per failing arm.
    //  **Validates: Requirements 4.1, 4.2, 4.3, 4.4, 4.5, 4.6, 9.1, 9.2, 9.3, 9.5**
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

    /// <summary>
    /// Generates a grammar where all arms correctly start with the prefix rule.
    /// Property: verification produces zero STORM0017 diagnostics.
    /// </summary>
    [Property(MaxTest = 80)]
    public Property P6_AllArmsStartWithPrefix_ZeroDiagnostics()
    {
        // Generate: unique prefix name, unique arm names, unique alt names,
        // unique tail token names. All arms begin with the prefix rule reference.
        var testGen =
            GenIdentifier().SelectMany(prefixName =>
            Gen.Choose(1, 4).SelectMany(armCount =>
            Gen.ListOf(GenIdentifier(), armCount).SelectMany(tailTokenNames =>
            Gen.ListOf(GenIdentifier(), armCount).SelectMany(armNames =>
            Gen.ListOf(GenIdentifier(), armCount).Select(altNames =>
                (prefixName, armCount, tailTokenNames: tailTokenNames.ToList(),
                 armNames: armNames.ToList(), altNames: altNames.ToList()))))));

        return Prop.ForAll(testGen.ToArbitrary(), drawn =>
        {
            var (prefixName, armCount, tailTokenNames, armNames, altNames) = drawn;
            // Ensure all names are distinct to avoid grammar conflicts
            var allNames = new HashSet<string>(StringComparer.Ordinal)
            {
                prefixName
            };
            var validArms = new List<(string armName, string altName, string tailToken)>();

            for(var i = 0; i < armCount; i++)
            {
                var armName = armNames[i];
                var altName = altNames[i];
                var tailToken = tailTokenNames[i];

                // Skip if any name collision
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

            // Build the grammar source
            var source = new System.Text.StringBuilder();
            _ = source.AppendLine($"token PrefixTok = \"p\";");
            _ = source.AppendLine($"rule {prefixName} = PrefixTok;");

            foreach(var (armName, _, tailToken) in validArms)
            {
                _ = source.AppendLine($"token {tailToken} = \"{tailToken.ToLowerInvariant()}\";");
                _ = source.AppendLine($"rule {armName} = {prefixName} {tailToken};");
            }

            var armsList = string.Join(", ", validArms.Select(a => $"{a.armName} as {a.altName}"));
            _ = source.AppendLine($"rule Container = inline {prefixName}: {{ {armsList} }};");

            var diagnostics = Verify(source.ToString());

            Assert.True(
                diagnostics.Count == 0,
                $"Expected zero diagnostics but got {diagnostics.Count}: " +
                $"{string.Join("; ", diagnostics.Select(d => d.Message))}\n" +
                $"Source:\n{source}");
        });
    }

    /// <summary>
    /// Generates a grammar where some arms do NOT start with the prefix rule.
    /// Property: exactly one STORM0017 per failing arm.
    /// </summary>
    [Property(MaxTest = 80)]
    public Property P6_ArmsNotStartingWithPrefix_ExactlyOneDiagnosticPerFailingArm()
    {
        var testGen =
            GenIdentifier().SelectMany(prefixName =>
            Gen.Choose(1, 4).SelectMany(armCount =>
            Gen.ListOf(Gen.Elements(true, false), armCount).SelectMany(startsWithPrefix =>
            Gen.ListOf(GenIdentifier(), armCount).SelectMany(armNames =>
            Gen.ListOf(GenIdentifier(), armCount).SelectMany(altNames =>
            Gen.ListOf(GenIdentifier(), armCount).Select(tailTokenNames =>
                (prefixName, armCount,
                 startsWithPrefix: startsWithPrefix.ToList(),
                 armNames: armNames.ToList(),
                 altNames: altNames.ToList(),
                 tailTokenNames: tailTokenNames.ToList())))))));

        return Prop.ForAll(testGen.ToArbitrary(), drawn =>
        {
            var (prefixName, armCount, startsWithPrefix, armNames, altNames, tailTokenNames) = drawn;
            // Ensure all names are distinct
            var allNames = new HashSet<string>(StringComparer.Ordinal)
            {
                prefixName, "PrefixTok", "WrongRule", "WrongTok"
            };
            var validArms = new List<(string armName, string altName, string tailToken, bool correct)>();

            for(var i = 0; i < armCount; i++)
            {
                var armName = armNames[i];
                var altName = altNames[i];
                var tailToken = tailTokenNames[i];

                if(!allNames.Add(armName) || !allNames.Add(altName) || !allNames.Add(tailToken))
                {
                    continue;
                }

                validArms.Add((armName, altName, tailToken, startsWithPrefix[i]));
            }

            if(validArms.Count == 0)
            {
                return;
            }

            // Build the grammar source
            var source = new System.Text.StringBuilder();
            _ = source.AppendLine($"token PrefixTok = \"p\";");
            _ = source.AppendLine($"token WrongTok = \"w\";");
            _ = source.AppendLine($"rule {prefixName} = PrefixTok;");
            _ = source.AppendLine($"rule WrongRule = WrongTok;");

            foreach(var (armName, _, tailToken, correct) in validArms)
            {
                _ = source.AppendLine($"token {tailToken} = \"{tailToken.ToLowerInvariant()}\";");
                _ = correct
                    ? source.AppendLine($"rule {armName} = {prefixName} {tailToken};")
                    : source.AppendLine($"rule {armName} = WrongRule {tailToken};");
            }

            var armsList = string.Join(", ", validArms.Select(a => $"{a.armName} as {a.altName}"));
            _ = source.AppendLine($"rule Container = inline {prefixName}: {{ {armsList} }};");

            var diagnostics = Verify(source.ToString());

            var expectedFailCount = validArms.Count(a => !a.correct);
            var storm0017Count = diagnostics.Count(d =>
                d.Id == StormDiagnosticIds.STORM_INLINE_PREFIX_MISMATCH);

            Assert.True(
                storm0017Count == expectedFailCount,
                $"Expected {expectedFailCount} STORM0017 diagnostics but got {storm0017Count}.\n" +
                $"Diagnostics: {string.Join("; ", diagnostics.Select(d => d.Message))}\n" +
                $"Source:\n{source}");

            // Each failing arm should have exactly one diagnostic mentioning its alt name
            foreach(var (_, altName, _, correct) in validArms)
            {
                if(!correct)
                {
                    Assert.Contains(diagnostics, d =>
                        d.Id == StormDiagnosticIds.STORM_INLINE_PREFIX_MISMATCH &&
                        d.Message.Contains(altName));
                }
            }
        });
    }
}