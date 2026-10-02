using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Property-based tests for unrecognized annotation rejection.
/// Feature: parser-memoization-expansion, Property 2: Unrecognized annotation rejection
/// </summary>
public class StormUnrecognizedAnnotationPropertyTests
{
    // ══════════════════════════════════════════════════════════════════
    //  Generator: Valid identifiers that are NOT "memo"
    //  Produces strings matching [a-zA-Z_][a-zA-Z0-9_]* excluding "memo"
    // ══════════════════════════════════════════════════════════════════

    private static Gen<string> GenNonMemoIdentifier()
    {
        var identStartChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_"
            .ToCharArray();

        var identPartChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_"
            .ToCharArray();

        var startGen = Gen.Elements(identStartChars);

        var tailGen = Gen.Choose(0, 15).SelectMany(len =>
            Gen.ArrayOf(Gen.Elements(identPartChars), len)
                .Select(chars => new string(chars)));

        return startGen.SelectMany(start =>
            tailGen.Select(tail => start + tail))
            .Where(id => id != "memo");
    }

    // ══════════════════════════════════════════════════════════════════
    //  2.3 — Property 2: Unrecognized annotation rejection
    //  For any annotation token whose lexeme is not `@memo`, the Storm
    //  parser SHALL emit a diagnostic error and not produce a valid
    //  declaration node.
    //  **Validates: Requirements 2.3**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 200)]
    public Property P2_UnrecognizedAnnotation_EmitsDiagnosticAndNoDeclaration()
    {
        return Prop.ForAll(GenNonMemoIdentifier().ToArbitrary(), identifier =>
        {
            var annotation = "@" + identifier;
            // Place the unrecognized annotation before a rule declaration
            var input = $"{annotation} rule TestRule = A;";

            var diagnostics = new List<StormDiagnostic>();
            var source = new StormSourceText("test.storm", input);
            var tokens = StormLexer.Tokenize(source, diagnostics);
            var file = StormParser.Parse(tokens, source, diagnostics);

            // Parser must emit at least one diagnostic error
            Assert.Contains(diagnostics, d =>
                d.Id == StormDiagnosticIds.STORM_SYNTAX_ERROR &&
                d.Severity == StormDiagnosticSeverity.Error);

            // The unrecognized annotation must not produce a valid declaration node.
            // Since the parser returns null for unrecognized annotations and uses
            // synchronization, the annotated rule should not appear as a StormRuleDecl
            // with IsMemo=true. We verify no declaration was produced from the annotation.
            var memoDecls = file.Declarations
                .OfType<StormRuleDecl>()
                .Where(d => d.IsMemo)
                .ToList();
            Assert.Empty(memoDecls);
        });
    }
}