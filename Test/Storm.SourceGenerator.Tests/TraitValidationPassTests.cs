using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Validate;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Unit tests for <see cref="TraitValidationPass"/> diagnostics (STORM0018-STORM0024)
/// and the success case where a valid trait binding produces no diagnostics.
/// </summary>
public class TraitValidationPassTests
{
    // ═══════════════════════════════════════════════════════════════════
    //  STORM0018: Unresolved trait field type
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void UnresolvedFieldType_EmitsSTORM0018()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "trait Foo = name:NonExistent;");

        var diagnostics = TraitValidationPass.Run(grammar);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_TRAIT_FIELD_UNRESOLVED &&
            d.Message.Contains("NonExistent") &&
            d.Message.Contains("Foo"));
    }

    [Fact]
    public void FragmentInTraitField_EmitsSTORM0018()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "fragment Bar = \"x\";\ntrait Foo = name:Bar;");

        var diagnostics = TraitValidationPass.Run(grammar);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_TRAIT_FIELD_UNRESOLVED &&
            d.Message.Contains("Bar") &&
            d.Message.Contains("Foo"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  STORM0019: Unresolved trait binding
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void UnresolvedTraitBinding_EmitsSTORM0019()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token Bar = \"bar\";\nrule Foo : NonExistent = name:Bar;");

        var diagnostics = TraitValidationPass.Run(grammar);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_TRAIT_BINDING_UNRESOLVED &&
            d.Message.Contains("NonExistent") &&
            d.Message.Contains("Foo"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  STORM0020: Missing field
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void MissingField_EmitsSTORM0020()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token X = \"x\";\ntoken Y = \"y\";\ntrait T = a:X b:Y;\nrule R : T = a:X;");

        var diagnostics = TraitValidationPass.Run(grammar);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_TRAIT_FIELD_MISSING &&
            d.Message.Contains("b") &&
            d.Message.Contains("T"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  STORM0021: Field type mismatch / cardinality mismatch
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void FieldTypeMismatch_EmitsSTORM0021()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token X = \"x\";\ntoken Y = \"y\";\ntrait T = a:X;\nrule R : T = a:Y;");

        var diagnostics = TraitValidationPass.Run(grammar);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_TRAIT_FIELD_MISMATCH &&
            d.Message.Contains("a") &&
            d.Message.Contains("T"));
    }

    [Fact]
    public void FieldCardinalityMismatch_EmitsSTORM0021()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token X = \"x\";\ntrait T = a:X*;\nrule R : T = a:X;");

        var diagnostics = TraitValidationPass.Run(grammar);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_TRAIT_FIELD_MISMATCH &&
            d.Message.Contains("a") &&
            d.Message.Contains("T"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  STORM0022: Multi-alternative rule cannot bind traits
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void MultiAlternativeRule_EmitsSTORM0022()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token X = \"x\";\ntoken Y = \"y\";\ntrait T = x:X;\nrule R : T = x:X as First | y:Y as Second;");

        var diagnostics = TraitValidationPass.Run(grammar);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_TRAIT_MULTI_ALT &&
            d.Message.Contains("R"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  STORM0023: Unresolved composed trait
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void UnresolvedComposedTrait_EmitsSTORM0023()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token Foo = \"foo\";\ntrait B : NonExistent = x:Foo;");

        var diagnostics = TraitValidationPass.Run(grammar);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_TRAIT_COMPOSED_UNRESOLVED &&
            d.Message.Contains("NonExistent") &&
            d.Message.Contains("B"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  STORM0024: Inline arm unsatisfied
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void InlineArmUnsatisfied_EmitsSTORM0024()
    {
        // TargetA has the required field, TargetB does not
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token X = \"x\";\n" +
            "token Y = \"y\";\n" +
            "trait T = name:X;\n" +
            "rule Prefix = Y;\n" +
            "rule TargetA = name:X;\n" +
            "rule TargetB = other:Y;\n" +
            "rule R : T = inline Prefix: { TargetA as ArmA, TargetB as ArmB };");

        var diagnostics = TraitValidationPass.Run(grammar);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_TRAIT_INLINE_ARM_UNSATISFIED &&
            d.Message.Contains("TargetB") &&
            d.Message.Contains("T") &&
            d.Message.Contains("R"));

        // TargetA satisfies the trait, so no diagnostic for it
        Assert.DoesNotContain(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_TRAIT_INLINE_ARM_UNSATISFIED &&
            d.Message.Contains("TargetA"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Success case: valid trait binding with correct fields
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ValidTraitBinding_NoDiagnostics()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token Ident = [a-z]+;\n" +
            "token Number = [0-9]+;\n" +
            "trait HasName = name:Ident;\n" +
            "rule Decl : HasName = name:Ident Number;");

        var diagnostics = TraitValidationPass.Run(grammar);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ValidTraitBindingWithCardinality_NoDiagnostics()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token Ident = [a-z]+;\n" +
            "rule Param = Ident;\n" +
            "trait HasParams = params:Param*;\n" +
            "rule Func : HasParams = params:Param* Ident;");

        var diagnostics = TraitValidationPass.Run(grammar);

        Assert.Empty(diagnostics);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Zero-field (marker) traits
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void MarkerTrait_NoDiagnostics()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token Ident = [a-z]+;\n" +
            "trait Marker;");

        var diagnostics = TraitValidationPass.Run(grammar);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void PureCompositionTrait_NoDiagnostics()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token Ident = [a-z]+;\n" +
            "token Number = [0-9]+;\n" +
            "trait HasName = name:Ident;\n" +
            "trait HasValue = value:Number;\n" +
            "trait Combined : HasName, HasValue;");

        var diagnostics = TraitValidationPass.Run(grammar);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void PureCompositionTrait_RuleBindsSatisfiesAllFields()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token Ident = [a-z]+;\n" +
            "token Number = [0-9]+;\n" +
            "trait HasName = name:Ident;\n" +
            "trait HasValue = value:Number;\n" +
            "trait Combined : HasName, HasValue;\n" +
            "rule Decl : Combined = name:Ident value:Number;");

        var diagnostics = TraitValidationPass.Run(grammar);

        Assert.Empty(diagnostics);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  STORM0025: Conflicting field definitions
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ConflictingFieldTypes_EmitsSTORM0025()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token Ident = [a-z]+;\n" +
            "token Number = [0-9]+;\n" +
            "trait A = name:Ident;\n" +
            "trait B = name:Number;\n" +
            "trait C : A, B;");

        var diagnostics = TraitValidationPass.Run(grammar);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_TRAIT_FIELD_CONFLICT &&
            d.Message.Contains("name") &&
            d.Message.Contains("C"));
    }

    [Fact]
    public void ConflictingFieldCardinality_EmitsSTORM0025()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token Ident = [a-z]+;\n" +
            "trait A = name:Ident;\n" +
            "trait B = name:Ident*;\n" +
            "trait C : A, B;");

        var diagnostics = TraitValidationPass.Run(grammar);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_TRAIT_FIELD_CONFLICT &&
            d.Message.Contains("name") &&
            d.Message.Contains("C"));
    }

    [Fact]
    public void DuplicateFieldSameSignature_NoDiagnostics()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token Ident = [a-z]+;\n" +
            "trait A = name:Ident;\n" +
            "trait B = name:Ident;\n" +
            "trait C : A, B;");

        var diagnostics = TraitValidationPass.Run(grammar);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void OwnFieldMatchesComposedField_NoDiagnostics()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token Ident = [a-z]+;\n" +
            "trait A = name:Ident;\n" +
            "trait B : A = name:Ident;");

        var diagnostics = TraitValidationPass.Run(grammar);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void OwnFieldConflictsWithComposed_EmitsSTORM0025()
    {
        var grammar = ValidationTestHelpers.BuildGrammar(
            "token Ident = [a-z]+;\n" +
            "token Number = [0-9]+;\n" +
            "trait A = name:Ident;\n" +
            "trait B : A = name:Number;");

        var diagnostics = TraitValidationPass.Run(grammar);

        Assert.Contains(diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_TRAIT_FIELD_CONFLICT &&
            d.Message.Contains("name") &&
            d.Message.Contains("B"));
    }
}