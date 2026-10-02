using System.Collections.Generic;
using Storm.SourceGenerator.Frontend;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.IR;

/// <summary>
/// Builds <see cref="TraitIR"/> objects from a resolved <see cref="StormGrammar"/>.
/// Runs after <see cref="Validate.TraitValidationPass"/> succeeds. Resolves field types
/// to determine whether they reference tokens or rules (needed for emission to produce
/// the correct C# property type).
/// </summary>
public static class TraitIRBuilder
{
    /// <summary>
    /// The result of building trait IR from a grammar.
    /// Contains both the trait definitions and the rule-to-traits mapping.
    /// </summary>
    public sealed class BuildResult
    {
        /// <summary>
        /// All trait declarations in the grammar, lowered to IR in declaration order.
        /// </summary>
        public IReadOnlyList<TraitIR> Traits { get; }

        /// <summary>
        /// Maps rule names to their bound traits (resolved, including propagated inline block traits).
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<TraitIR>> RuleTraitBindings { get; }

        public BuildResult(IReadOnlyList<TraitIR> traits,
                          IReadOnlyDictionary<string, IReadOnlyList<TraitIR>> ruleTraitBindings)
        {
            Traits = traits;
            RuleTraitBindings = ruleTraitBindings;
        }
    }

    /// <summary>
    /// Builds trait IR from the given grammar. The grammar must have passed
    /// <see cref="Validate.TraitValidationPass"/> without errors before calling this method.
    /// </summary>
    public static BuildResult Build(StormGrammar grammar)
    {
        // Step 1: Build TraitIR for each StormTraitDecl (resolve field types to IsToken flag)
        var traitIRsByName = new Dictionary<string, TraitIR>();
        var traitDeclOrder = new List<StormTraitDecl>();

        foreach(var decl in grammar.Declarations)
        {
            if(decl is StormTraitDecl traitDecl)
            {
                traitDeclOrder.Add(traitDecl);
            }
        }

        // Build TraitIR objects. Composed traits are resolved by name, so we build
        // in declaration order and resolve composed references after all are built.
        // First pass: build without composed trait references.
        foreach(var traitDecl in traitDeclOrder)
        {
            var fields = new List<TraitFieldIR>();
            foreach(var field in traitDecl.Fields)
            {
                var isToken = ResolveIsToken(field.TypeName, grammar);
                fields.Add(new TraitFieldIR(field.FieldName, field.TypeName, isToken, field.Cardinality));
            }

            // Placeholder with empty composed traits; resolved in second pass
            var traitIR = new TraitIR(traitDecl.Name, fields, new List<TraitIR>());
            traitIRsByName[traitDecl.Name] = traitIR;
        }

        // Second pass: resolve composed trait references
        var traits = new List<TraitIR>();
        foreach(var traitDecl in traitDeclOrder)
        {
            var composedTraits = new List<TraitIR>();
            foreach(var composedName in traitDecl.ComposedTraits)
            {
                if(traitIRsByName.TryGetValue(composedName, out var composedIR))
                {
                    composedTraits.Add(composedIR);
                }
            }

            // Rebuild with resolved composed traits
            var existing = traitIRsByName[traitDecl.Name];
            var resolved = new TraitIR(existing.Name, existing.Fields, composedTraits);
            traitIRsByName[traitDecl.Name] = resolved;
            traits.Add(resolved);
        }

        // Step 2: Build rule→traits mapping for each StormRuleDecl with TraitBindings
        var ruleTraitBindings = new Dictionary<string, List<TraitIR>>();

        foreach(var decl in grammar.Declarations)
        {
            if(decl is StormRuleDecl { TraitBindings.Count: > 0 } ruleDecl)
            {
                var resolvedTraits = ResolveBindings(ruleDecl.TraitBindings!, traitIRsByName);
                if(resolvedTraits.Count > 0)
                {
                    AddTraitsToRule(ruleTraitBindings, ruleDecl.Name, resolvedTraits);

                    // Step 3: Resolve inline block propagation
                    PropagateInlineBlockTraits(ruleDecl, resolvedTraits, grammar, ruleTraitBindings);
                }
            }
        }

        // Step 4: Build per-alternative trait bindings (as Name : Trait)
        foreach(var decl in grammar.Declarations)
        {
            if(decl is StormRuleDecl ruleDecl && ruleDecl.Body is StormAlternationExpr altExpr)
            {
                foreach(var alt in altExpr.Alternatives)
                {
                    if(alt is StormNamedAlternative { TraitBindings.Count: > 0 } namedAlt)
                    {
                        var resolvedTraits = ResolveBindings(namedAlt.TraitBindings!, traitIRsByName);
                        if(resolvedTraits.Count > 0)
                        {
                            // Key format: "RuleName.AlternativeName" to distinguish from rule-level bindings
                            var key = $"{ruleDecl.Name}.{namedAlt.AlternativeName}";
                            AddTraitsToRule(ruleTraitBindings, key, resolvedTraits);
                        }
                    }
                }
            }
        }

        // Convert to IReadOnlyList values
        var finalBindings = new Dictionary<string, IReadOnlyList<TraitIR>>();
        foreach(var kvp in ruleTraitBindings)
        {
            finalBindings[kvp.Key] = kvp.Value;
        }

        return new BuildResult(traits, finalBindings);
    }

    private static bool ResolveIsToken(string typeName, StormGrammar grammar)
    {
        var resolved = grammar.LookupByName(typeName);
        return resolved is StormTokenDecl or StormHiddenTokenDecl;
    }

    private static List<TraitIR> ResolveBindings(
        IReadOnlyList<string> bindingNames,
        Dictionary<string, TraitIR> traitIRsByName)
    {
        var resolved = new List<TraitIR>();
        foreach(var name in bindingNames)
        {
            if(traitIRsByName.TryGetValue(name, out var traitIR))
            {
                resolved.Add(traitIR);
            }
        }

        return resolved;
    }

    private static void PropagateInlineBlockTraits(
        StormRuleDecl ruleDecl,
        List<TraitIR> resolvedTraits,
        StormGrammar grammar,
        Dictionary<string, List<TraitIR>> ruleTraitBindings)
    {
        var inlineBlock = GetInlineBlockExpr(ruleDecl.Body);
        if(inlineBlock == null)
        {
            return;
        }

        foreach(var arm in inlineBlock.Arms)
        {
            var targetName = GetArmTargetRuleName(arm);
            if(targetName == null)
            {
                continue;
            }

            // Verify the target is actually a rule in the grammar
            var targetDecl = grammar.LookupByName(targetName);
            if(targetDecl is not StormRuleDecl)
            {
                continue;
            }

            AddTraitsToRule(ruleTraitBindings, targetName, resolvedTraits);
        }
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

    /// <summary>
    /// Adds traits to a rule's binding list, deduplicating by trait name.
    /// </summary>
    private static void AddTraitsToRule(
        Dictionary<string, List<TraitIR>> ruleTraitBindings,
        string ruleName,
        List<TraitIR> traitsToAdd)
    {
        if(!ruleTraitBindings.TryGetValue(ruleName, out var existing))
        {
            existing = new List<TraitIR>();
            ruleTraitBindings[ruleName] = existing;
        }

        foreach(var trait in traitsToAdd)
        {
            var alreadyPresent = false;
            foreach(var existingTrait in existing)
            {
                if(existingTrait.Name == trait.Name)
                {
                    alreadyPresent = true;
                    break;
                }
            }

            if(!alreadyPresent)
            {
                existing.Add(trait);
            }
        }
    }
}