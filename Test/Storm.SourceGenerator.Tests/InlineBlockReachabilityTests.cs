using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;
using Storm.SourceGenerator.Validate;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Unit and property-based tests for the reachability pass with inline blocks.
/// Validates: Requirements 5.1, 5.2, 5.3, 5.4, 5.5
/// </summary>
public class InlineBlockReachabilityTests
{
    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static List<StormDiagnostic> RunReachability(string source)
    {
        var grammar = ValidationTestHelpers.BuildGrammar(source);
        return ReachabilityPass.Run(grammar);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Inline block is opaque for sibling analysis
    //  (No false STORM0006 against siblings)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void InlineBlock_OpaqueForSiblings_NoFalseSTORM0006()
    {
        // The inline block's prefix starts with token A.
        // Sibling alternative starts with token C (different from A).
        // No STORM0006 should be emitted for the inline block or the sibling.
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            token D = "d";
            rule Prefix = A B;
            rule Arm1 = Prefix C;
            rule Arm2 = Prefix D;
            rule R = inline Prefix: { Arm1 as Alt1, Arm2 as Alt2 } | C;
            """;

        var diagnostics = RunReachability(source);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void InlineBlock_OpaqueForSiblings_MultipleSiblings_NoWarning()
    {
        // Inline block prefix starts with A. Siblings start with B, C.
        // All FIRST-k sets are distinct, so no STORM0006.
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            token D = "d";
            token E = "e";
            rule Prefix = A;
            rule Arm1 = Prefix D;
            rule Arm2 = Prefix E;
            rule R = B | inline Prefix: { Arm1 as Alt1, Arm2 as Alt2 } | C;
            """;

        var diagnostics = RunReachability(source);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void InlineBlock_SiblingOverlapsWithPrefix_EmitsSTORM0006ForSibling()
    {
        // The inline block prefix starts with token A (single-token prefix).
        // A later sibling is also just token A (same FIRST-k sequence).
        // The sibling should be flagged as unreachable.
        var source = """
            token A = "a";
            token B = "b";
            rule Prefix = A;
            rule Arm1 = Prefix B;
            rule R = inline Prefix: { Arm1 as Alt1 } | A;
            """;

        var diagnostics = RunReachability(source);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_UNREACHABLE_ALTERNATIVE &&
            d.Severity == StormDiagnosticSeverity.Warning);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Tail divergence — arms with distinct FIRST tokens
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void InlineBlock_TailDivergence_DistinctFirstTokens_NoWarning()
    {
        // Arm1 tail starts with C, Arm2 tail starts with D — distinct, no warning.
        // Sibling E ensures intra-block analysis is triggered.
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            token D = "d";
            token E = "e";
            rule Prefix = A B;
            rule Arm1 = Prefix C;
            rule Arm2 = Prefix D;
            rule R = inline Prefix: { Arm1 as Alt1, Arm2 as Alt2 } | E;
            """;

        var diagnostics = RunReachability(source);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void InlineBlock_ThreeArms_AllDistinctTails_NoWarning()
    {
        // Three arms with distinct tail FIRST tokens — no warning.
        // Sibling E ensures intra-block analysis is triggered.
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            token D = "d";
            token E = "e";
            rule Prefix = A;
            rule Arm1 = Prefix B;
            rule Arm2 = Prefix C;
            rule Arm3 = Prefix D;
            rule R = inline Prefix: { Arm1 as Alt1, Arm2 as Alt2, Arm3 as Alt3 } | E;
            """;

        var diagnostics = RunReachability(source);

        Assert.Empty(diagnostics);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: Later arm with subset tail FIRST-k emits STORM0006
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void InlineBlock_LaterArmSubsetTail_EmitsSTORM0006()
    {
        // Both arms have the same tail FIRST token (C). The second arm is
        // unreachable because its tail FIRST-k is a subset of the first arm's.
        // Note: the inline block must be one of multiple alternatives for
        // CheckInlineBlock to be called (CheckRule returns early for single alt).
        var source = """
            token A = "a";
            token C = "c";
            token X = "x";
            rule Prefix = A;
            rule Arm1 = Prefix C;
            rule Arm2 = Prefix C;
            rule R = inline Prefix: { Arm1 as Alt1, Arm2 as Alt2 } | X;
            """;

        var diagnostics = RunReachability(source);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_UNREACHABLE_ALTERNATIVE &&
            d.Severity == StormDiagnosticSeverity.Warning &&
            d.Message.Contains("Alt2"));
    }

    [Fact]
    public void InlineBlock_ThirdArmSubsetOfFirstTwoUnion_EmitsSTORM0006()
    {
        // Arm1 tail starts with B, Arm2 tail starts with C, Arm3 tail starts with B.
        // Arm3's tail FIRST-k is a subset of the union of Arm1+Arm2 tails.
        // Needs a sibling for CheckInlineBlock to be called.
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            token X = "x";
            rule Prefix = A;
            rule Arm1 = Prefix B;
            rule Arm2 = Prefix C;
            rule Arm3 = Prefix B;
            rule R = inline Prefix: { Arm1 as Alt1, Arm2 as Alt2, Arm3 as Alt3 } | X;
            """;

        var diagnostics = RunReachability(source);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_UNREACHABLE_ALTERNATIVE &&
            d.Severity == StormDiagnosticSeverity.Warning &&
            d.Message.Contains("Alt3"));
        // Alt1 and Alt2 should not be flagged
        Assert.DoesNotContain(diagnostics, d =>
            d.Message.Contains("Alt1"));
        Assert.DoesNotContain(diagnostics, d =>
            d.Message.Contains("Alt2"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unit Tests: FIRST-k of inline block as whole equals prefix FIRST-k
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void InlineBlock_FirstKEqualsPrefix_SiblingWithSameTokenFlagged()
    {
        // The inline block's FIRST-k should be computed from the prefix rule (starts with A).
        // A sibling alternative also starting with A placed after the block is unreachable.
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            rule Prefix = A;
            rule Arm1 = Prefix B;
            rule Arm2 = Prefix C;
            rule R = inline Prefix: { Arm1 as Alt1, Arm2 as Alt2 } | A;
            """;

        var diagnostics = RunReachability(source);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_UNREACHABLE_ALTERNATIVE &&
            d.Severity == StormDiagnosticSeverity.Warning);
    }

    [Fact]
    public void InlineBlock_FirstKEqualsPrefix_MultiTokenPrefix()
    {
        // The prefix starts with "A B". The inline block's FIRST-k should be "A B ...".
        // A sibling alternative starting with "A C" should NOT be flagged
        // (distinct at k=2).
        var source = """
            token A = "a";
            token B = "b";
            token C = "c";
            token D = "d";
            rule Prefix = A B;
            rule Arm1 = Prefix C;
            rule R = inline Prefix: { Arm1 as Alt1 } | A C D;
            """;

        var diagnostics = RunReachability(source);

        Assert.Empty(diagnostics);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Property 7: Reachability -- Inline Block Opaque for Sibling Analysis
    //  Feature: inline-rule-prefix, Property 7: Reachability -- Inline Block Opaque for Sibling Analysis
    //
    //  For any rule containing an inline block alongside other alternatives
    //  where the inline block's prefix FIRST-k set does NOT overlap with
    //  those siblings' FIRST-k sets, the reachability pass SHALL emit zero
    //  STORM0006 warnings for the inline block alternative or its sibling
    //  alternatives.
    //  **Validates: Requirements 5.1**
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
    /// Generates grammars where an inline block's prefix FIRST-k does NOT overlap
    /// with sibling alternatives. Asserts zero STORM0006.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property P7_InlineBlockOpaque_NoOverlapWithSiblings_ZeroSTORM0006()
    {
        // Strategy: generate unique token names for:
        // - prefixToken (starts the prefix rule)
        // - armTailTokens (tail tokens for each arm, distinct from each other)
        // - siblingTokens (FIRST tokens for siblings, distinct from prefixToken)
        // This guarantees no overlap between the inline block and siblings.

        var testGen =
            Gen.Choose(1, 3).SelectMany(armCount =>
            Gen.Choose(1, 3).SelectMany(siblingCount =>
            GenIdentifier().SelectMany(prefixName =>
            Gen.ListOf(GenIdentifier(), armCount).SelectMany(armNames =>
            Gen.ListOf(GenIdentifier(), armCount).SelectMany(altNames =>
            Gen.ListOf(GenIdentifier(), armCount).SelectMany(tailTokenNames =>
            Gen.ListOf(GenIdentifier(), siblingCount).Select(siblingTokenNames =>
                (prefixName, armCount, siblingCount,
                 armNames: armNames.ToList(),
                 altNames: altNames.ToList(),
                 tailTokenNames: tailTokenNames.ToList(),
                 siblingTokenNames: siblingTokenNames.ToList()))))))));

        return Prop.ForAll(testGen.ToArbitrary(), drawn =>
        {
            var (prefixName, armCount, siblingCount, armNames, altNames, tailTokenNames, siblingTokenNames) = drawn;
            // Ensure all names are distinct
            var allNames = new HashSet<string>(StringComparer.Ordinal)
            {
                prefixName, "PrefixTok"
            };

            var validArms = new List<(string armName, string altName, string tailToken)>();
            for(var i = 0; i < armCount; i++)
            {
                var armName = armNames[i];
                var altName = altNames[i];
                var tailToken = tailTokenNames[i];
                if(!allNames.Add(armName) || !allNames.Add(altName) || !allNames.Add(tailToken))
                {
                    continue;
                }

                validArms.Add((armName, altName, tailToken));
            }

            var validSiblings = new List<string>();
            foreach(var sibToken in siblingTokenNames)
            {
                if(allNames.Add(sibToken))
                {
                    validSiblings.Add(sibToken);
                }
            }

            if(validArms.Count == 0 || validSiblings.Count == 0)
            {
                return;
            }

            // Build grammar: prefix starts with PrefixTok, siblings start with their own tokens
            var source = new System.Text.StringBuilder();
            _ = source.AppendLine("token PrefixTok = \"p\";");
            _ = source.AppendLine($"rule {prefixName} = PrefixTok;");

            foreach(var (armName, _, tailToken) in validArms)
            {
                _ = source.AppendLine($"token {tailToken} = \"{tailToken.ToLowerInvariant()}\";");
                _ = source.AppendLine($"rule {armName} = {prefixName} {tailToken};");
            }

            foreach(var sibToken in validSiblings)
            {
                _ = source.AppendLine($"token {sibToken} = \"{sibToken.ToLowerInvariant()}\";");
            }

            var armsList = string.Join(", ", validArms.Select(a => $"{a.armName} as {a.altName}"));
            var siblingAlts = string.Join(" | ", validSiblings.Select(s => s));
            _ = source.AppendLine($"rule Container = inline {prefixName}: {{ {armsList} }} | {siblingAlts};");

            var diagnostics = RunReachability(source.ToString());

            // No STORM0006 should be emitted since prefix FIRST-k (PrefixTok)
            // doesn't overlap with sibling tokens
            var storm0006 = diagnostics.Where(d =>
                d.Id == StormDiagnosticIds.STORM_UNREACHABLE_ALTERNATIVE).ToList();

            Assert.True(
                storm0006.Count == 0,
                $"Expected zero STORM0006 but got {storm0006.Count}: " +
                $"{string.Join("; ", storm0006.Select(d => d.Message))}\n" +
                $"Source:\n{source}");
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Property 8: Reachability -- Intra-Block Tail Divergence
    //  Feature: inline-rule-prefix, Property 8: Reachability -- Intra-Block Tail Divergence
    //
    //  For any inline block whose arms have mutually distinct tail FIRST-k
    //  sets, zero STORM0006. If a later arm's tail FIRST-k IS a subset of
    //  earlier arms' union, STORM0006 is emitted.
    //  **Validates: Requirements 5.2, 5.3**
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Generates inline blocks where all arm tails have distinct FIRST tokens.
    /// Asserts zero STORM0006 for the arms.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property P8_AllArmTailsDistinct_ZeroSTORM0006()
    {
        var testGen =
            Gen.Choose(2, 5).SelectMany(armCount =>
            GenIdentifier().SelectMany(prefixName =>
            Gen.ListOf(GenIdentifier(), armCount).SelectMany(armNames =>
            Gen.ListOf(GenIdentifier(), armCount).SelectMany(altNames =>
            Gen.ListOf(GenIdentifier(), armCount).SelectMany(tailTokenNames =>
            GenIdentifier().Select(siblingToken =>
                (prefixName, armCount,
                 armNames: armNames.ToList(),
                 altNames: altNames.ToList(),
                 tailTokenNames: tailTokenNames.ToList(),
                 siblingToken)))))));

        return Prop.ForAll(testGen.ToArbitrary(), drawn =>
        {
            var (prefixName, armCount, armNames, altNames, tailTokenNames, siblingToken) = drawn;
            var allNames = new HashSet<string>(StringComparer.Ordinal)
            {
                prefixName, "PrefixTok"
            };

            var validArms = new List<(string armName, string altName, string tailToken)>();
            for(var i = 0; i < armCount; i++)
            {
                var armName = armNames[i];
                var altName = altNames[i];
                var tailToken = tailTokenNames[i];
                if(!allNames.Add(armName) || !allNames.Add(altName) || !allNames.Add(tailToken))
                {
                    continue;
                }

                validArms.Add((armName, altName, tailToken));
            }

            // Need a sibling token distinct from all others for CheckInlineBlock to trigger
            if(!allNames.Add(siblingToken) || validArms.Count < 2)
            {
                return;
            }

            var source = new System.Text.StringBuilder();
            _ = source.AppendLine("token PrefixTok = \"p\";");
            _ = source.AppendLine($"rule {prefixName} = PrefixTok;");

            foreach(var (armName, _, tailToken) in validArms)
            {
                _ = source.AppendLine($"token {tailToken} = \"{tailToken.ToLowerInvariant()}\";");
                _ = source.AppendLine($"rule {armName} = {prefixName} {tailToken};");
            }

            _ = source.AppendLine($"token {siblingToken} = \"{siblingToken.ToLowerInvariant()}\";");

            var armsList = string.Join(", ", validArms.Select(a => $"{a.armName} as {a.altName}"));
            _ = source.AppendLine($"rule Container = inline {prefixName}: {{ {armsList} }} | {siblingToken};");

            var diagnostics = RunReachability(source.ToString());

            var storm0006 = diagnostics.Where(d =>
                d.Id == StormDiagnosticIds.STORM_UNREACHABLE_ALTERNATIVE).ToList();

            Assert.True(
                storm0006.Count == 0,
                $"Expected zero STORM0006 but got {storm0006.Count}: " +
                $"{string.Join("; ", storm0006.Select(d => d.Message))}\n" +
                $"Source:\n{source}");
        });
    }

    /// <summary>
    /// Generates inline blocks where a later arm has a tail FIRST-k that duplicates
    /// an earlier arm's. Asserts STORM0006 for the duplicating arm.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property P8_LaterArmTailSubset_EmitsSTORM0006()
    {
        // Strategy: generate N-1 arms with distinct tail tokens, then add a final
        // arm whose tail reuses one of the earlier tokens (making it a subset).
        // A sibling alternative is required for CheckInlineBlock to be invoked.
        var testGen =
            Gen.Choose(2, 4).SelectMany(armCount =>
            GenIdentifier().SelectMany(prefixName =>
            Gen.ListOf(GenIdentifier(), armCount + 1).SelectMany(armNames =>
            Gen.ListOf(GenIdentifier(), armCount + 1).SelectMany(altNames =>
            Gen.ListOf(GenIdentifier(), armCount - 1).SelectMany(distinctTailTokens =>
            GenIdentifier().SelectMany(siblingToken =>
            Gen.Choose(0, armCount - 2).Select(duplicateIndex =>
                (prefixName, armCount,
                 armNames: armNames.ToList(),
                 altNames: altNames.ToList(),
                 distinctTailTokens: distinctTailTokens.ToList(),
                 siblingToken,
                 duplicateIndex))))))));

        return Prop.ForAll(testGen.ToArbitrary(), drawn =>
        {
            var (prefixName, armCount, armNames, altNames, distinctTailTokens, siblingToken, duplicateIndex) = drawn;
            var allNames = new HashSet<string>(StringComparer.Ordinal)
            {
                prefixName, "PrefixTok"
            };

            // Build distinct tail tokens for first N-1 arms
            var validDistinctTokens = new List<string>();
            foreach(var tok in distinctTailTokens)
            {
                if(allNames.Add(tok))
                {
                    validDistinctTokens.Add(tok);
                }
            }

            if(validDistinctTokens.Count < 2)
            {
                return;
            }

            // Need a distinct sibling token
            if(!allNames.Add(siblingToken))
            {
                return;
            }

            // Build arms: first N-1 with distinct tokens, last arm duplicates one
            var validArms = new List<(string armName, string altName, string tailToken)>();
            for(var i = 0; i < validDistinctTokens.Count; i++)
            {
                var armName = armNames.Count > i ? armNames[i] : $"Arm{i}";
                var altName = altNames.Count > i ? altNames[i] : $"Alt{i}";
                if(!allNames.Add(armName) || !allNames.Add(altName))
                {
                    continue;
                }

                validArms.Add((armName, altName, validDistinctTokens[i]));
            }

            if(validArms.Count < 2)
            {
                return;
            }

            // Add the duplicating arm (last arm uses a token from an earlier arm)
            var safeIndex = duplicateIndex % validArms.Count;
            var dupToken = validArms[safeIndex].tailToken;
            var lastArmName = "DupArm";
            var lastAltName = "DupAlt";
            if(!allNames.Add(lastArmName) || !allNames.Add(lastAltName))
            {
                return;
            }

            validArms.Add((lastArmName, lastAltName, dupToken));

            var source = new System.Text.StringBuilder();
            _ = source.AppendLine("token PrefixTok = \"p\";");
            _ = source.AppendLine($"rule {prefixName} = PrefixTok;");

            // Declare each distinct tail token only once
            var declaredTokens = new HashSet<string>(StringComparer.Ordinal);
            foreach(var (armName, _, tailToken) in validArms)
            {
                if(declaredTokens.Add(tailToken))
                {
                    _ = source.AppendLine($"token {tailToken} = \"{tailToken.ToLowerInvariant()}\";");
                }

                _ = source.AppendLine($"rule {armName} = {prefixName} {tailToken};");
            }

            _ = source.AppendLine($"token {siblingToken} = \"{siblingToken.ToLowerInvariant()}\";");

            var armsList = string.Join(", ", validArms.Select(a => $"{a.armName} as {a.altName}"));
            _ = source.AppendLine($"rule Container = inline {prefixName}: {{ {armsList} }} | {siblingToken};");

            var diagnostics = RunReachability(source.ToString());

            // The last arm (DupAlt) should be flagged
            var storm0006 = diagnostics.Where(d =>
                d.Id == StormDiagnosticIds.STORM_UNREACHABLE_ALTERNATIVE).ToList();

            Assert.True(
                storm0006.Count > 0,
                $"Expected at least one STORM0006 for duplicating arm but got none.\n" +
                $"Source:\n{source}");
            Assert.Contains(storm0006, d => d.Message.Contains(lastAltName));
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Property 9: Reachability -- Block FIRST-k Equals Prefix FIRST-k
    //  Feature: inline-rule-prefix, Property 9: Reachability -- Block FIRST-k Equals Prefix FIRST-k
    //
    //  For any rule containing an inline block, the FIRST-k set computed for
    //  the inline block as a whole SHALL equal the FIRST-k set of the prefix
    //  clause's referenced rule.
    //  **Validates: Requirements 5.4**
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that the FIRST-k set computed for an inline block expression equals
    /// the FIRST-k set computed directly from the prefix rule.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property P9_BlockFirstKEqualsPrefixFirstK()
    {
        // Strategy: generate grammars with an inline block and verify that
        // ComputeFirstKSequences(inlineBlock) == ComputeFirstKSequences(prefixRule.Body)
        var testGen =
            Gen.Choose(1, 3).SelectMany(armCount =>
            Gen.Choose(1, 3).SelectMany(prefixTokenCount =>
            GenIdentifier().SelectMany(prefixName =>
            Gen.ListOf(GenIdentifier(), armCount).SelectMany(armNames =>
            Gen.ListOf(GenIdentifier(), armCount).SelectMany(altNames =>
            Gen.ListOf(GenIdentifier(), armCount).SelectMany(tailTokenNames =>
            Gen.ListOf(GenIdentifier(), prefixTokenCount).Select(prefixTokenNames =>
                (prefixName, armCount, prefixTokenCount,
                 armNames: armNames.ToList(),
                 altNames: altNames.ToList(),
                 tailTokenNames: tailTokenNames.ToList(),
                 prefixTokenNames: prefixTokenNames.ToList()))))))));

        return Prop.ForAll(testGen.ToArbitrary(), drawn =>
        {
            var (prefixName, armCount, prefixTokenCount, armNames, altNames, tailTokenNames, prefixTokenNames) = drawn;
            var allNames = new HashSet<string>(StringComparer.Ordinal)
            {
                prefixName
            };

            // Validate prefix tokens are unique
            var validPrefixTokens = new List<string>();
            foreach(var tok in prefixTokenNames)
            {
                if(allNames.Add(tok))
                {
                    validPrefixTokens.Add(tok);
                }
            }

            if(validPrefixTokens.Count == 0)
            {
                return;
            }

            var validArms = new List<(string armName, string altName, string tailToken)>();
            for(var i = 0; i < armCount; i++)
            {
                var armName = armNames[i];
                var altName = altNames[i];
                var tailToken = tailTokenNames[i];
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

            // Build grammar
            var source = new System.Text.StringBuilder();
            foreach(var tok in validPrefixTokens)
            {
                _ = source.AppendLine($"token {tok} = \"{tok.ToLowerInvariant()}\";");
            }

            var prefixBody = string.Join(" ", validPrefixTokens);
            _ = source.AppendLine($"rule {prefixName} = {prefixBody};");

            foreach(var (armName, _, tailToken) in validArms)
            {
                _ = source.AppendLine($"token {tailToken} = \"{tailToken.ToLowerInvariant()}\";");
                _ = source.AppendLine($"rule {armName} = {prefixName} {tailToken};");
            }

            var armsList = string.Join(", ", validArms.Select(a => $"{a.armName} as {a.altName}"));
            _ = source.AppendLine($"rule Container = inline {prefixName}: {{ {armsList} }};");

            // Build grammar and extract the inline block + prefix rule
            var grammar = ValidationTestHelpers.BuildGrammar(source.ToString());

            // Find the Container rule
            var containerDecl = grammar.Declarations
                .OfType<StormRuleDecl>()
                .First(r => r.Name == "Container");

            // The body should contain the inline block
            var topAlts = ReachabilityPass.GetTopLevelAlternatives(containerDecl.Body);
            var inlineBlockExpr = topAlts
                .Select(a => a is StormNamedAlternative na ? na.Expression : a)
                .OfType<StormInlineBlockExpr>()
                .FirstOrDefault();

            if(inlineBlockExpr == null)
            {
                return;
            }

            // Compute FIRST-k for the inline block (should use prefix FIRST-k)
            var blockFirstK = ReachabilityPass.ComputeFirstKSequences(inlineBlockExpr, grammar, 3);

            // Compute FIRST-k directly from the prefix rule's body
            var prefixDecl = grammar.Declarations
                .OfType<StormRuleDecl>()
                .First(r => r.Name == prefixName);
            var prefixFirstK = ReachabilityPass.ComputeFirstKSequences(prefixDecl.Body, grammar, 3);

            Assert.True(
                blockFirstK.SetEquals(prefixFirstK),
                $"Block FIRST-k {{{string.Join(", ", blockFirstK)}}} != " +
                $"Prefix FIRST-k {{{string.Join(", ", prefixFirstK)}}}\n" +
                $"Source:\n{source}");
        });
    }
}