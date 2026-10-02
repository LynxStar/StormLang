using System.Collections.Generic;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Validate;

/// <summary>
/// Nominal prefix verification pass for inline blocks. Walks all rule bodies
/// finding <see cref="StormInlineBlockExpr"/> nodes and verifies that each arm's
/// target rule begins with a reference to the declared prefix rule.
/// Emits STORM0017 on mismatch.
/// </summary>
public static class InlinePrefixVerification
{
    public static List<StormDiagnostic> Verify(StormGrammar grammar)
    {
        var diagnostics = new List<StormDiagnostic>();

        foreach(var decl in grammar.Declarations)
        {
            WalkExpression(decl.Body, grammar, diagnostics);
        }

        return diagnostics;
    }

    private static void WalkExpression(
        StormExpression expr,
        StormGrammar grammar,
        List<StormDiagnostic> diagnostics)
    {
        switch(expr)
        {
            case StormInlineBlockExpr inlineBlock:
                VerifyInlineBlock(inlineBlock, grammar, diagnostics);
                break;

            case StormSequenceExpr seqExpr:
                foreach(var element in seqExpr.Elements)
                {
                    WalkExpression(element, grammar, diagnostics);
                }

                break;

            case StormAlternationExpr altExpr:
                foreach(var alt in altExpr.Alternatives)
                {
                    WalkExpression(alt, grammar, diagnostics);
                }

                break;

            case StormOptionalExpr optExpr:
                WalkExpression(optExpr.Inner, grammar, diagnostics);
                break;

            case StormZeroOrMoreExpr zomExpr:
                WalkExpression(zomExpr.Inner, grammar, diagnostics);
                break;

            case StormOneOrMoreExpr oomExpr:
                WalkExpression(oomExpr.Inner, grammar, diagnostics);
                break;

            case StormGroupExpr groupExpr:
                WalkExpression(groupExpr.Inner, grammar, diagnostics);
                break;

            case StormLabeledExpr labeledExpr:
                WalkExpression(labeledExpr.Element, grammar, diagnostics);
                break;

            case StormNamedAlternative namedAlt:
                WalkExpression(namedAlt.Expression, grammar, diagnostics);
                break;

            case StormReferenceExpr:
            case StormQualifiedReferenceExpr:
            case StormStringLiteralExpr:
            case StormCharClassExpr:
            case StormRegexLiteralExpr:
                // Leaf nodes — nothing to walk
                break;
        }
    }

    private static void VerifyInlineBlock(
        StormInlineBlockExpr inlineBlock,
        StormGrammar grammar,
        List<StormDiagnostic> diagnostics)
    {
        // The Prefix is always a StormReferenceExpr naming the prefix rule.
        // If it is not, a parse error occurred but recovery continued — reject it.
        if(inlineBlock.Prefix is not StormReferenceExpr prefixRef)
        {
            diagnostics.Add(new StormDiagnostic(
                StormDiagnosticIds.STORM_INLINE_PREFIX_MISMATCH,
                StormDiagnosticSeverity.Error,
                "Inline block prefix must be a rule reference (e.g., 'inline MyPrefixRule: { ... }'). Anonymous inline prefixes are not supported.",
                null,
                inlineBlock.Line,
                inlineBlock.Column));
            return;
        }

        var prefixRuleName = prefixRef.Name;

        // Resolve the prefix rule — must exist
        var prefixDecl = grammar.LookupByName(prefixRuleName);
        if(prefixDecl == null)
        {
            diagnostics.Add(new StormDiagnostic(
                StormDiagnosticIds.STORM_INLINE_PREFIX_MISMATCH,
                StormDiagnosticSeverity.Error,
                $"Inline block references undefined prefix rule '{prefixRuleName}'",
                null,
                inlineBlock.Line,
                inlineBlock.Column));
            return;
        }

        // The prefix rule must have a single-sequence body (not an alternation)
        if(prefixDecl.Body is StormAlternationExpr)
        {
            diagnostics.Add(new StormDiagnostic(
                StormDiagnosticIds.STORM_INLINE_PREFIX_MISMATCH,
                StormDiagnosticSeverity.Error,
                $"Prefix rule '{prefixRuleName}' has an alternation body; inline block prefix rules must have a single-sequence body",
                null,
                inlineBlock.Line,
                inlineBlock.Column));
            return;
        }

        // Verify each arm
        foreach(var arm in inlineBlock.Arms)
        {
            VerifyArm(arm, prefixRuleName, inlineBlock, grammar, diagnostics);
        }
    }

    private static void VerifyArm(
        StormInlineArm arm,
        string prefixRuleName,
        StormInlineBlockExpr inlineBlock,
        StormGrammar grammar,
        List<StormDiagnostic> diagnostics)
    {
        // If the arm's expression is a nested inline block, recursively verify it
        if(arm.Expression is StormInlineBlockExpr nestedBlock)
        {
            VerifyInlineBlock(nestedBlock, grammar, diagnostics);
            return;
        }

        // The arm's Expression should be a StormReferenceExpr naming the arm's target rule
        if(arm.Expression is not StormReferenceExpr armRef)
        {
            return;
        }

        var armRuleName = armRef.Name;

        // Resolve the arm's target rule — must exist
        var armDecl = grammar.LookupByName(armRuleName);
        if(armDecl == null)
        {
            diagnostics.Add(new StormDiagnostic(
                StormDiagnosticIds.STORM_INLINE_PREFIX_MISMATCH,
                StormDiagnosticSeverity.Error,
                $"Arm '{arm.AlternativeName}' in inline block references undefined rule '{armRuleName}'",
                null,
                inlineBlock.Line,
                inlineBlock.Column));
            return;
        }

        // The arm's rule must have a single-sequence body (not alternation)
        if(armDecl.Body is StormAlternationExpr)
        {
            diagnostics.Add(new StormDiagnostic(
                StormDiagnosticIds.STORM_INLINE_PREFIX_MISMATCH,
                StormDiagnosticSeverity.Error,
                $"Arm '{arm.AlternativeName}' in inline block references rule '{armRuleName}' which has an alternation body; arm rules must have a single-sequence body",
                null,
                inlineBlock.Line,
                inlineBlock.Column));
            return;
        }

        // Check if the arm rule's leading element(s) match the prefix.
        // Two valid patterns:
        // 1. First element is a reference to the prefix rule (possibly labeled)
        // 2. Leading elements structurally match the prefix rule's body elements
        if(ArmStartsWithPrefixReference(armDecl.Body, prefixRuleName))
        {
            return; // Valid: arm starts with a reference to the prefix rule
        }

        // Check structural match of leading elements against prefix body
        var prefixDecl = grammar.LookupByName(prefixRuleName);
        if(prefixDecl != null && ArmStartsWithPrefixElements(armDecl.Body, prefixDecl.Body))
        {
            return; // Valid: arm's leading elements match the prefix body
        }

        diagnostics.Add(new StormDiagnostic(
            StormDiagnosticIds.STORM_INLINE_PREFIX_MISMATCH,
            StormDiagnosticSeverity.Error,
            $"Arm '{arm.AlternativeName}' in inline block references rule '{armRuleName}' which does not begin with prefix rule '{prefixRuleName}'.",
            null,
            inlineBlock.Line,
            inlineBlock.Column));
    }

    /// <summary>
    /// Checks if the arm rule's body starts with a reference to the prefix rule
    /// (possibly wrapped in a label).
    /// </summary>
    private static bool ArmStartsWithPrefixReference(StormExpression body, string prefixRuleName)
    {
        StormExpression firstElement;

        if(body is StormSequenceExpr seq)
        {
            if(seq.Elements.Count == 0)
            {
                return false;
            }

            firstElement = seq.Elements[0];
        }
        else
        {
            firstElement = body;
        }

        // Unwrap label if present
        if(firstElement is StormLabeledExpr labeled)
        {
            firstElement = labeled.Element;
        }

        return firstElement is StormReferenceExpr refExpr && refExpr.Name == prefixRuleName;
    }

    /// <summary>
    /// Checks if the arm rule's leading elements structurally match the prefix rule's body.
    /// Used when the arm has the prefix elements inline rather than as a single rule reference.
    /// </summary>
    private static bool ArmStartsWithPrefixElements(StormExpression armBody, StormExpression prefixBody)
    {
        var prefixElements = GetBodyElements(prefixBody);
        var armElements = GetBodyElements(armBody);

        if(armElements.Count < prefixElements.Count)
        {
            return false;
        }

        for(var i = 0; i < prefixElements.Count; i++)
        {
            if(!ElementsMatch(prefixElements[i], armElements[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets the body elements of an expression as a list. If the body is a
    /// sequence, returns its elements. Otherwise returns a single-element list.
    /// </summary>
    private static List<StormExpression> GetBodyElements(StormExpression body)
    {
        if(body is StormSequenceExpr seq)
        {
            return new List<StormExpression>(seq.Elements);
        }

        return new List<StormExpression> { body };
    }

    /// <summary>
    /// Checks if two grammar elements match structurally (ignoring labels).
    /// Both are unwrapped from labels before comparison.
    /// </summary>
    private static bool ElementsMatch(StormExpression prefixElem, StormExpression armElem)
    {
        // Unwrap labels
        var a = UnwrapLabel(prefixElem);
        var b = UnwrapLabel(armElem);

        // Compare the unwrapped expressions structurally
        return ExpressionsMatchStructurally(a, b);
    }

    private static StormExpression UnwrapLabel(StormExpression expr)
    {
        if(expr is StormLabeledExpr labeled)
        {
            return labeled.Element;
        }

        return expr;
    }

    private static bool ExpressionsMatchStructurally(StormExpression a, StormExpression b)
    {
        return (a, b) switch
        {
            (StormReferenceExpr refA, StormReferenceExpr refB) => refA.Name == refB.Name,
            (StormStringLiteralExpr litA, StormStringLiteralExpr litB) => litA.Value == litB.Value,
            (StormOneOrMoreExpr oomA, StormOneOrMoreExpr oomB) => ExpressionsMatchStructurally(oomA.Inner, oomB.Inner),
            (StormZeroOrMoreExpr zomA, StormZeroOrMoreExpr zomB) => ExpressionsMatchStructurally(zomA.Inner, zomB.Inner),
            (StormOptionalExpr optA, StormOptionalExpr optB) => ExpressionsMatchStructurally(optA.Inner, optB.Inner),
            _ => false,
        };
    }
}