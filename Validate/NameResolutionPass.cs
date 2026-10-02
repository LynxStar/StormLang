using System.Collections.Generic;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Validate;

/// <summary>
/// Walks all expressions in the effective grammar and verifies every reference
/// resolves to a declared rule/token/fragment. Rejects fragment references from
/// rule expressions (fragments are lexer-only, R4.5). Emits STORM_UNDEFINED_REFERENCE
/// diagnostics for unresolved references.
/// </summary>
public static class NameResolutionPass
{
    public static List<StormDiagnostic> Run(StormGrammar grammar)
    {
        var diagnostics = new List<StormDiagnostic>();

        foreach(var decl in grammar.Declarations)
        {
            if(decl is StormTraitDecl)
            {
                continue; // Traits have no body expressions to resolve
            }

            var isRuleContext = decl is StormRuleDecl;
            WalkExpression(decl.Body, grammar, isRuleContext, decl, diagnostics);
        }

        return diagnostics;
    }

    private static void WalkExpression(
        StormExpression expr,
        StormGrammar grammar,
        bool isRuleContext,
        StormDeclaration containingDecl,
        List<StormDiagnostic> diagnostics)
    {
        switch(expr)
        {
            case StormReferenceExpr refExpr:
                var resolved = grammar.LookupByName(refExpr.Name);
                if(resolved == null)
                {
                    diagnostics.Add(new StormDiagnostic(
                        StormDiagnosticIds.STORM_UNDEFINED_REFERENCE,
                        StormDiagnosticSeverity.Error,
                        $"Undefined reference '{refExpr.Name}' in declaration '{containingDecl.Name}'",
                        null,
                        containingDecl.Line,
                        containingDecl.Column));
                }
                else if(isRuleContext && resolved is StormFragmentDecl)
                {
                    diagnostics.Add(new StormDiagnostic(
                        StormDiagnosticIds.STORM_UNDEFINED_REFERENCE,
                        StormDiagnosticSeverity.Error,
                        $"Fragment '{refExpr.Name}' cannot be referenced from rule '{containingDecl.Name}'; fragments are lexer-only",
                        null,
                        containingDecl.Line,
                        containingDecl.Column));
                }

                break;

            case StormQualifiedReferenceExpr qualRef:
                var qualifiedName = qualRef.Qualifier + "." + qualRef.Name;
                var qualResolved = grammar.LookupByName(qualifiedName);
                if(qualResolved == null)
                {
                    diagnostics.Add(new StormDiagnostic(
                        StormDiagnosticIds.STORM_UNDEFINED_REFERENCE,
                        StormDiagnosticSeverity.Error,
                        $"Undefined qualified reference '{qualifiedName}' in declaration '{containingDecl.Name}'",
                        null,
                        containingDecl.Line,
                        containingDecl.Column));
                }
                else if(isRuleContext && qualResolved is StormFragmentDecl)
                {
                    diagnostics.Add(new StormDiagnostic(
                        StormDiagnosticIds.STORM_UNDEFINED_REFERENCE,
                        StormDiagnosticSeverity.Error,
                        $"Fragment '{qualifiedName}' cannot be referenced from rule '{containingDecl.Name}'; fragments are lexer-only",
                        null,
                        containingDecl.Line,
                        containingDecl.Column));
                }

                break;

            case StormSequenceExpr seqExpr:
                foreach(var element in seqExpr.Elements)
                {
                    WalkExpression(element, grammar, isRuleContext, containingDecl, diagnostics);
                }

                break;

            case StormAlternationExpr altExpr:
                foreach(var alt in altExpr.Alternatives)
                {
                    WalkExpression(alt, grammar, isRuleContext, containingDecl, diagnostics);
                }

                break;

            case StormOptionalExpr optExpr:
                WalkExpression(optExpr.Inner, grammar, isRuleContext, containingDecl, diagnostics);
                break;

            case StormZeroOrMoreExpr zomExpr:
                WalkExpression(zomExpr.Inner, grammar, isRuleContext, containingDecl, diagnostics);
                break;

            case StormOneOrMoreExpr oomExpr:
                WalkExpression(oomExpr.Inner, grammar, isRuleContext, containingDecl, diagnostics);
                break;

            case StormGroupExpr groupExpr:
                WalkExpression(groupExpr.Inner, grammar, isRuleContext, containingDecl, diagnostics);
                break;

            case StormPredicateExpr predicateExpr:
                WalkExpression(predicateExpr.Inner, grammar, isRuleContext, containingDecl, diagnostics);
                break;

            case StormLabeledExpr labeledExpr:
                WalkExpression(labeledExpr.Element, grammar, isRuleContext, containingDecl, diagnostics);
                break;

            case StormNamedAlternative namedAlt:
                WalkExpression(namedAlt.Expression, grammar, isRuleContext, containingDecl, diagnostics);
                break;

            case StormStringLiteralExpr:
            case StormCharClassExpr:
                // Leaf nodes — no references to resolve
                break;
        }
    }
}