using System.Collections.Generic;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Validate;

/// <summary>
/// Validates trait declarations and trait bindings on rules after name resolution succeeds.
/// Checks field type resolution, composed trait resolution, binding resolution,
/// structural satisfaction, multi-alternative restriction, and inline block propagation.
/// </summary>
public static class TraitValidationPass
{
    public static List<StormDiagnostic> Run(StormGrammar grammar)
    {
        var diagnostics = new List<StormDiagnostic>();

        // Collect all trait declarations
        var traitsByName = new Dictionary<string, StormTraitDecl>();
        foreach(var decl in grammar.Declarations)
        {
            if(decl is StormTraitDecl traitDecl)
            {
                traitsByName[traitDecl.Name] = traitDecl;
            }
        }

        // Validate trait field type references and composed trait references
        foreach(var trait in traitsByName.Values)
        {
            ValidateTraitFields(trait, grammar, diagnostics);
            ValidateComposedTraits(trait, traitsByName, diagnostics);
        }

        // Validate rule trait bindings
        foreach(var decl in grammar.Declarations)
        {
            if(decl is StormRuleDecl { TraitBindings.Count: > 0 } ruleDecl)
            {
                ValidateRuleTraitBindings(ruleDecl, traitsByName, grammar, diagnostics);
            }
        }

        // Validate per-alternative trait bindings
        foreach(var decl in grammar.Declarations)
        {
            if(decl is StormRuleDecl ruleDecl)
            {
                ValidateAlternativeTraitBindings(ruleDecl, traitsByName, diagnostics);
            }
        }

        return diagnostics;
    }

    private static void ValidateTraitFields(
        StormTraitDecl trait,
        StormGrammar grammar,
        List<StormDiagnostic> diagnostics)
    {
        foreach(var field in trait.Fields)
        {
            var resolved = grammar.LookupByName(field.TypeName);
            if(resolved == null)
            {
                diagnostics.Add(new StormDiagnostic(
                    StormDiagnosticIds.STORM_TRAIT_FIELD_UNRESOLVED,
                    StormDiagnosticSeverity.Error,
                    $"Trait field type '{field.TypeName}' in trait '{trait.Name}' cannot be resolved to a token or rule",
                    null,
                    trait.Line,
                    trait.Column));
            }
            else if(resolved is StormFragmentDecl)
            {
                diagnostics.Add(new StormDiagnostic(
                    StormDiagnosticIds.STORM_TRAIT_FIELD_UNRESOLVED,
                    StormDiagnosticSeverity.Error,
                    $"Trait field type '{field.TypeName}' in trait '{trait.Name}' cannot be resolved to a token or rule",
                    null,
                    trait.Line,
                    trait.Column));
            }
        }
    }

    private static void ValidateComposedTraits(
        StormTraitDecl trait,
        Dictionary<string, StormTraitDecl> traitsByName,
        List<StormDiagnostic> diagnostics)
    {
        foreach(var composedName in trait.ComposedTraits)
        {
            if(!traitsByName.ContainsKey(composedName))
            {
                diagnostics.Add(new StormDiagnostic(
                    StormDiagnosticIds.STORM_TRAIT_COMPOSED_UNRESOLVED,
                    StormDiagnosticSeverity.Error,
                    $"Trait '{trait.Name}' composes '{composedName}' which is not a declared trait",
                    null,
                    trait.Line,
                    trait.Column));
            }
        }

        // Check for conflicting field definitions across composed traits and own fields
        ValidateFieldConflicts(trait, traitsByName, diagnostics);
    }

    private static void ValidateFieldConflicts(
        StormTraitDecl trait,
        Dictionary<string, StormTraitDecl> traitsByName,
        List<StormDiagnostic> diagnostics)
    {
        var seen = new Dictionary<string, StormTraitField>();

        // Collect all fields from composed traits (recursively)
        foreach(var composedName in trait.ComposedTraits)
        {
            if(!traitsByName.TryGetValue(composedName, out var composedTrait))
            {
                continue;
            }

            var composedFields = CollectAllFields(composedTrait, traitsByName);
            foreach(var field in composedFields)
            {
                if(seen.TryGetValue(field.FieldName, out var existing))
                {
                    if(existing.TypeName != field.TypeName || existing.Cardinality != field.Cardinality)
                    {
                        diagnostics.Add(new StormDiagnostic(
                            StormDiagnosticIds.STORM_TRAIT_FIELD_CONFLICT,
                            StormDiagnosticSeverity.Error,
                            $"Trait '{trait.Name}' has conflicting definitions for field '{field.FieldName}' from composed traits",
                            null,
                            trait.Line,
                            trait.Column));
                    }
                }
                else
                {
                    seen[field.FieldName] = field;
                }
            }
        }

        // Own fields override composed fields of the same name, but must match if present in composed
        foreach(var field in trait.Fields)
        {
            if(seen.TryGetValue(field.FieldName, out var existing))
            {
                if(existing.TypeName != field.TypeName || existing.Cardinality != field.Cardinality)
                {
                    diagnostics.Add(new StormDiagnostic(
                        StormDiagnosticIds.STORM_TRAIT_FIELD_CONFLICT,
                        StormDiagnosticSeverity.Error,
                        $"Trait '{trait.Name}' has conflicting definitions for field '{field.FieldName}' from composed traits",
                        null,
                        trait.Line,
                        trait.Column));
                }
            }
        }
    }

    private static void ValidateRuleTraitBindings(
        StormRuleDecl ruleDecl,
        Dictionary<string, StormTraitDecl> traitsByName,
        StormGrammar grammar,
        List<StormDiagnostic> diagnostics)
    {
        // Resolve each binding name to a declared trait
        var resolvedTraits = new List<StormTraitDecl>();
        foreach(var bindingName in ruleDecl.TraitBindings!)
        {
            if(!traitsByName.TryGetValue(bindingName, out var traitDecl))
            {
                diagnostics.Add(new StormDiagnostic(
                    StormDiagnosticIds.STORM_TRAIT_BINDING_UNRESOLVED,
                    StormDiagnosticSeverity.Error,
                    $"Trait binding '{bindingName}' on rule '{ruleDecl.Name}' cannot be resolved to a declared trait",
                    null,
                    ruleDecl.Line,
                    ruleDecl.Column));
            }
            else
            {
                resolvedTraits.Add(traitDecl);
            }
        }

        if(resolvedTraits.Count == 0)
        {
            return;
        }

        // Multi-alternative check: if the rule body is an alternation with named alternatives
        if(IsMultiAlternativeRule(ruleDecl.Body))
        {
            diagnostics.Add(new StormDiagnostic(
                StormDiagnosticIds.STORM_TRAIT_MULTI_ALT,
                StormDiagnosticSeverity.Error,
                $"Rule '{ruleDecl.Name}' has named alternatives and cannot bind traits",
                null,
                ruleDecl.Line,
                ruleDecl.Column));
            return;
        }

        // Inline block propagation validation
        if(HasInlineBlockBody(ruleDecl.Body))
        {
            ValidateInlineBlockPropagation(ruleDecl, resolvedTraits, traitsByName, grammar, diagnostics);
            return;
        }

        // Structural satisfaction: validate that the rule's labeled fields satisfy each trait
        var ruleFields = ExtractLabeledFields(ruleDecl.Body);
        foreach(var trait in resolvedTraits)
        {
            ValidateStructuralSatisfaction(ruleDecl, trait, traitsByName, ruleFields, diagnostics);
        }
    }

    private static void ValidateAlternativeTraitBindings(
        StormRuleDecl ruleDecl,
        Dictionary<string, StormTraitDecl> traitsByName,
        List<StormDiagnostic> diagnostics)
    {
        if(ruleDecl.Body is not StormAlternationExpr altExpr)
        {
            return;
        }

        foreach(var alt in altExpr.Alternatives)
        {
            if(alt is not StormNamedAlternative { TraitBindings.Count: > 0 } namedAlt)
            {
                continue;
            }

            // Resolve trait binding names
            var resolvedTraits = new List<StormTraitDecl>();
            foreach(var bindingName in namedAlt.TraitBindings!)
            {
                if(!traitsByName.TryGetValue(bindingName, out var traitDecl))
                {
                    diagnostics.Add(new StormDiagnostic(
                        StormDiagnosticIds.STORM_TRAIT_BINDING_UNRESOLVED,
                        StormDiagnosticSeverity.Error,
                        $"Trait binding '{bindingName}' on alternative '{namedAlt.AlternativeName}' of rule '{ruleDecl.Name}' cannot be resolved to a declared trait",
                        null,
                        ruleDecl.Line,
                        ruleDecl.Column));
                }
                else
                {
                    resolvedTraits.Add(traitDecl);
                }
            }

            if(resolvedTraits.Count == 0)
            {
                continue;
            }

            // Validate structural satisfaction of the alternative's labeled fields
            var altFields = ExtractLabeledFields(namedAlt.Expression);
            foreach(var trait in resolvedTraits)
            {
                ValidateAlternativeSatisfaction(ruleDecl, namedAlt, trait, traitsByName, altFields, diagnostics);
            }
        }
    }

    private static void ValidateAlternativeSatisfaction(
        StormRuleDecl ruleDecl,
        StormNamedAlternative namedAlt,
        StormTraitDecl trait,
        Dictionary<string, StormTraitDecl> traitsByName,
        Dictionary<string, (string TypeName, StormFieldCardinality Cardinality)> altFields,
        List<StormDiagnostic> diagnostics)
    {
        var allTraitFields = CollectAllFields(trait, traitsByName);
        foreach(var traitField in allTraitFields)
        {
            if(!altFields.TryGetValue(traitField.FieldName, out var altField))
            {
                diagnostics.Add(new StormDiagnostic(
                    StormDiagnosticIds.STORM_TRAIT_FIELD_MISSING,
                    StormDiagnosticSeverity.Error,
                    $"Alternative '{namedAlt.AlternativeName}' of rule '{ruleDecl.Name}' binds trait '{trait.Name}' but is missing field '{traitField.FieldName}'",
                    null,
                    ruleDecl.Line,
                    ruleDecl.Column));
            }
            else if(altField.TypeName != traitField.TypeName || altField.Cardinality != traitField.Cardinality)
            {
                var expected = FormatFieldSignature(traitField.TypeName, traitField.Cardinality);
                var actual = FormatFieldSignature(altField.TypeName, altField.Cardinality);
                diagnostics.Add(new StormDiagnostic(
                    StormDiagnosticIds.STORM_TRAIT_FIELD_MISMATCH,
                    StormDiagnosticSeverity.Error,
                    $"Alternative '{namedAlt.AlternativeName}' of rule '{ruleDecl.Name}' field '{traitField.FieldName}' has type '{actual}' but trait '{trait.Name}' requires '{expected}'",
                    null,
                    ruleDecl.Line,
                    ruleDecl.Column));
            }
        }
    }

    private static bool IsMultiAlternativeRule(StormExpression body)
    {
        if(body is not StormAlternationExpr altExpr)
        {
            return false;
        }

        foreach(var alt in altExpr.Alternatives)
        {
            if(alt is StormNamedAlternative)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasInlineBlockBody(StormExpression body)
    {
        // Direct inline block
        if(body is StormInlineBlockExpr)
        {
            return true;
        }

        // Inline block inside a named alternative within an alternation
        if(body is StormAlternationExpr altExpr)
        {
            foreach(var alt in altExpr.Alternatives)
            {
                var expr = alt is StormNamedAlternative na ? na.Expression : alt;
                if(expr is StormInlineBlockExpr)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static StormInlineBlockExpr? GetInlineBlockExpr(StormExpression body)
    {
        if(body is StormInlineBlockExpr ib)
        {
            return ib;
        }

        if(body is StormAlternationExpr altExpr)
        {
            foreach(var alt in altExpr.Alternatives)
            {
                var expr = alt is StormNamedAlternative na ? na.Expression : alt;
                if(expr is StormInlineBlockExpr inlineBlock)
                {
                    return inlineBlock;
                }
            }
        }

        return null;
    }

    private static void ValidateInlineBlockPropagation(
        StormRuleDecl ruleDecl,
        List<StormTraitDecl> resolvedTraits,
        Dictionary<string, StormTraitDecl> traitsByName,
        StormGrammar grammar,
        List<StormDiagnostic> diagnostics)
    {
        var inlineBlock = GetInlineBlockExpr(ruleDecl.Body);
        if(inlineBlock == null)
        {
            return;
        }

        foreach(var arm in inlineBlock.Arms)
        {
            // Get the target rule name from the arm's expression
            var targetName = GetArmTargetRuleName(arm);
            if(targetName == null)
            {
                continue;
            }

            var targetDecl = grammar.LookupByName(targetName) as StormRuleDecl;
            if(targetDecl == null)
            {
                continue; // Name resolution pass catches undefined references
            }

            // Validate that the target rule satisfies each trait
            var targetFields = ExtractLabeledFields(targetDecl.Body);
            foreach(var trait in resolvedTraits)
            {
                if(!SatisfiesTrait(trait, traitsByName, targetFields))
                {
                    diagnostics.Add(new StormDiagnostic(
                        StormDiagnosticIds.STORM_TRAIT_INLINE_ARM_UNSATISFIED,
                        StormDiagnosticSeverity.Error,
                        $"Inline arm target rule '{targetName}' does not satisfy trait '{trait.Name}' bound on rule '{ruleDecl.Name}'",
                        null,
                        ruleDecl.Line,
                        ruleDecl.Column));
                }
            }
        }
    }

    private static string? GetArmTargetRuleName(StormInlineArm arm)
    {
        if(arm.Expression is StormReferenceExpr refExpr)
        {
            return refExpr.Name;
        }

        // Labeled arm: label:RuleRef
        if(arm.Expression is StormLabeledExpr { Element: StormReferenceExpr labeledRef })
        {
            return labeledRef.Name;
        }

        return null;
    }

    private static void ValidateStructuralSatisfaction(
        StormRuleDecl ruleDecl,
        StormTraitDecl trait,
        Dictionary<string, StormTraitDecl> traitsByName,
        Dictionary<string, (string TypeName, StormFieldCardinality Cardinality)> ruleFields,
        List<StormDiagnostic> diagnostics)
    {
        var allTraitFields = CollectAllFields(trait, traitsByName);
        foreach(var traitField in allTraitFields)
        {
            if(!ruleFields.TryGetValue(traitField.FieldName, out var ruleField))
            {
                diagnostics.Add(new StormDiagnostic(
                    StormDiagnosticIds.STORM_TRAIT_FIELD_MISSING,
                    StormDiagnosticSeverity.Error,
                    $"Rule '{ruleDecl.Name}' binds trait '{trait.Name}' but is missing field '{traitField.FieldName}'",
                    null,
                    ruleDecl.Line,
                    ruleDecl.Column));
            }
            else if(ruleField.TypeName != traitField.TypeName || ruleField.Cardinality != traitField.Cardinality)
            {
                var expected = FormatFieldSignature(traitField.TypeName, traitField.Cardinality);
                var actual = FormatFieldSignature(ruleField.TypeName, ruleField.Cardinality);
                diagnostics.Add(new StormDiagnostic(
                    StormDiagnosticIds.STORM_TRAIT_FIELD_MISMATCH,
                    StormDiagnosticSeverity.Error,
                    $"Rule '{ruleDecl.Name}' field '{traitField.FieldName}' has type '{actual}' but trait '{trait.Name}' requires '{expected}'",
                    null,
                    ruleDecl.Line,
                    ruleDecl.Column));
            }
        }
    }

    private static bool SatisfiesTrait(
        StormTraitDecl trait,
        Dictionary<string, StormTraitDecl> traitsByName,
        Dictionary<string, (string TypeName, StormFieldCardinality Cardinality)> ruleFields)
    {
        var allTraitFields = CollectAllFields(trait, traitsByName);
        foreach(var traitField in allTraitFields)
        {
            if(!ruleFields.TryGetValue(traitField.FieldName, out var ruleField))
            {
                return false;
            }

            if(ruleField.TypeName != traitField.TypeName || ruleField.Cardinality != traitField.Cardinality)
            {
                return false;
            }
        }

        return true;
    }

    private static List<StormTraitField> CollectAllFields(
        StormTraitDecl trait,
        Dictionary<string, StormTraitDecl> traitsByName)
    {
        var seen = new Dictionary<string, StormTraitField>();

        // Composed trait fields come first (recursively)
        foreach(var composedName in trait.ComposedTraits)
        {
            if(traitsByName.TryGetValue(composedName, out var composedTrait))
            {
                foreach(var field in CollectAllFields(composedTrait, traitsByName))
                {
                    if(!seen.ContainsKey(field.FieldName))
                    {
                        seen[field.FieldName] = field;
                    }
                }
            }
        }

        // Then own fields (override composed fields of the same name)
        foreach(var field in trait.Fields)
        {
            seen[field.FieldName] = field;
        }

        return new List<StormTraitField>(seen.Values);
    }

    private static Dictionary<string, (string TypeName, StormFieldCardinality Cardinality)> ExtractLabeledFields(
        StormExpression body)
    {
        var fields = new Dictionary<string, (string TypeName, StormFieldCardinality Cardinality)>();
        WalkForLabels(body, fields);
        return fields;
    }

    private static void WalkForLabels(
        StormExpression expr,
        Dictionary<string, (string TypeName, StormFieldCardinality Cardinality)> fields)
    {
        switch(expr)
        {
            case StormLabeledExpr labeledExpr:
                var (typeName, cardinality) = ExtractTypeAndCardinality(labeledExpr.Element);
                if(typeName != null)
                {
                    fields[labeledExpr.Label] = (typeName, cardinality);
                }

                break;

            case StormSequenceExpr seqExpr:
                foreach(var element in seqExpr.Elements)
                {
                    WalkForLabels(element, fields);
                }

                break;

            case StormAlternationExpr altExpr:
                foreach(var alt in altExpr.Alternatives)
                {
                    WalkForLabels(alt, fields);
                }

                break;

            case StormOptionalExpr optExpr:
                WalkForLabels(optExpr.Inner, fields);
                break;

            case StormZeroOrMoreExpr zomExpr:
                WalkForLabels(zomExpr.Inner, fields);
                break;

            case StormOneOrMoreExpr oomExpr:
                WalkForLabels(oomExpr.Inner, fields);
                break;

            case StormGroupExpr groupExpr:
                WalkForLabels(groupExpr.Inner, fields);
                break;

            case StormNamedAlternative namedAlt:
                WalkForLabels(namedAlt.Expression, fields);
                break;
        }
    }

    private static (string? TypeName, StormFieldCardinality Cardinality) ExtractTypeAndCardinality(
        StormExpression expr)
    {
        return expr switch
        {
            StormReferenceExpr refExpr => (refExpr.Name, StormFieldCardinality.One),
            StormOptionalExpr { Inner: StormReferenceExpr optRef } => (optRef.Name, StormFieldCardinality.Optional),
            StormZeroOrMoreExpr { Inner: StormReferenceExpr zomRef } => (zomRef.Name, StormFieldCardinality.ZeroOrMore),
            StormOneOrMoreExpr { Inner: StormReferenceExpr oomRef } => (oomRef.Name, StormFieldCardinality.OneOrMore),
            _ => (null, StormFieldCardinality.One),
        };
    }

    private static string FormatFieldSignature(string typeName, StormFieldCardinality cardinality)
    {
        return cardinality switch
        {
            StormFieldCardinality.Optional => $"{typeName}?",
            StormFieldCardinality.ZeroOrMore => $"{typeName}*",
            StormFieldCardinality.OneOrMore => $"{typeName}+",
            _ => typeName,
        };
    }
}