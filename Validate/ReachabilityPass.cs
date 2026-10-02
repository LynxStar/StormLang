using System.Collections.Generic;
using System.Linq;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Validate;

/// <summary>An inline block's arms are compared by FIRST-k, not <see cref="AlternativeShadowing"/>, as inline-rule-prefix requirement 5.3 prescribes.</summary>
public static class ReachabilityPass
{
    /// <summary>
    /// Maximum lookahead depth for FIRST-k sequence computation.
    /// Deeper lookahead catches more cases but is more expensive.
    /// A depth of 3 handles the vast majority of grammar patterns.
    /// </summary>
    private const int MaxLookahead = 3;

    public static List<StormDiagnostic> Run(StormGrammar grammar)
    {
        var diagnostics = new List<StormDiagnostic>();

        foreach(var decl in grammar.Declarations)
        {
            if(decl is StormRuleDecl ruleDecl)
            {
                CheckRule(ruleDecl, grammar, diagnostics);
            }
        }

        return diagnostics;
    }

    private static void CheckRule(
        StormRuleDecl rule,
        StormGrammar grammar,
        List<StormDiagnostic> diagnostics)
    {
        var alternatives = GetTopLevelAlternatives(rule.Body);
        if(alternatives.Count <= 1)
        {
            return;
        }

        var shadowing = new AlternativeShadowing(grammar);
        for(var i = 0; i < alternatives.Count; i++)
        {
            if(i > 0 && shadowing.IsShadowed(alternatives, i))
            {
                diagnostics.Add(new StormDiagnostic(
                    StormDiagnosticIds.STORM_UNREACHABLE_ALTERNATIVE,
                    StormDiagnosticSeverity.Warning,
                    $"Alternative {i + 1} in rule '{rule.Name}' is unreachable; an earlier alternative succeeds on every input it can match",
                    null,
                    rule.Line,
                    rule.Column));
            }

            // Perform intra-block analysis for inline blocks in top-level alternatives
            var altExpr = alternatives[i];
            if(altExpr is StormNamedAlternative namedAlt)
            {
                altExpr = namedAlt.Expression;
            }

            if(altExpr is StormInlineBlockExpr inlineBlock)
            {
                CheckInlineBlock(inlineBlock, rule, grammar, diagnostics);
            }
        }
    }

    /// <summary>
    /// Performs intra-block reachability analysis for an inline block.
    /// For each arm, computes FIRST-k of the tail (arm's rule body minus the prefix
    /// elements). The prefix length is determined by resolving the inline block's
    /// prefix rule and counting its body elements. If a later arm's tail FIRST-k set
    /// is a subset of the union of all earlier arms' tail FIRST-k sets, emits STORM0006.
    /// Handles nested inline blocks recursively.
    /// </summary>
    private static void CheckInlineBlock(
        StormInlineBlockExpr inlineBlock,
        StormRuleDecl rule,
        StormGrammar grammar,
        List<StormDiagnostic> diagnostics)
    {
        var tailSequenceSets = new List<HashSet<string>>();

        foreach(var arm in inlineBlock.Arms)
        {
            // If the arm is a nested inline block, recursively check it
            if(arm.Expression is StormInlineBlockExpr nestedBlock)
            {
                CheckInlineBlock(nestedBlock, rule, grammar, diagnostics);
                // For nested blocks, compute the FIRST-k of the nested block's prefix
                // as the tail contribution (opaque treatment)
                var nestedPrefixSet = ComputeFirstKSequences(nestedBlock.Prefix, grammar, MaxLookahead);
                tailSequenceSets.Add(nestedPrefixSet);
                continue;
            }

            // The arm's Expression should be a StormReferenceExpr naming the target rule
            if(arm.Expression is not StormReferenceExpr armRef)
            {
                tailSequenceSets.Add(new HashSet<string>());
                continue;
            }

            // Resolve the arm's target rule
            var armDecl = grammar.LookupByName(armRef.Name);
            if(armDecl is not StormRuleDecl armRuleDecl)
            {
                tailSequenceSets.Add(new HashSet<string>());
                continue;
            }

            // Get the tail: the arm's rule body minus the prefix elements.
            // The prefix may appear as a single rule reference or as inline elements.
            var armPrefixLength = GetArmPrefixLength(armRuleDecl.Body, inlineBlock.Prefix, grammar);
            var tailExpr = GetTailExpression(armRuleDecl.Body, armPrefixLength);
            var tailSet = ComputeFirstKSequences(tailExpr, grammar, MaxLookahead);
            tailSequenceSets.Add(tailSet);
        }

        // Check each arm's tail against the union of earlier arms' tails
        var tailUnionSoFar = new HashSet<string>();
        for(var i = 0; i < inlineBlock.Arms.Count; i++)
        {
            if(i > 0 && tailSequenceSets[i].Count > 0 && tailSequenceSets[i].IsSubsetOf(tailUnionSoFar))
            {
                var arm = inlineBlock.Arms[i];
                diagnostics.Add(new StormDiagnostic(
                    StormDiagnosticIds.STORM_UNREACHABLE_ALTERNATIVE,
                    StormDiagnosticSeverity.Warning,
                    $"Arm '{arm.AlternativeName}' in inline block of rule '{rule.Name}' is unreachable; its tail lookahead sequences are covered by earlier arms",
                    null,
                    inlineBlock.Line,
                    inlineBlock.Column));
            }

            foreach(var seq in tailSequenceSets[i])
            {
                _ = tailUnionSoFar.Add(seq);
            }
        }
    }

    /// <summary>
    /// Determines how many leading elements in an arm's body correspond to the inline
    /// block's prefix. Two cases:
    /// 1. The arm starts with a single reference to the prefix rule → strip 1 element.
    /// 2. The arm's leading elements structurally match the prefix rule's body → strip N elements.
    /// Falls back to 1 if neither case matches cleanly.
    /// </summary>
    private static int GetArmPrefixLength(StormExpression armBody, StormExpression prefixExpr, StormGrammar grammar)
    {
        // Get the arm's body elements
        var armElements = armBody is StormSequenceExpr seq ? seq.Elements : new List<StormExpression> { armBody };
        if(armElements.Count == 0)
        {
            return 0;
        }

        // Case 1: The first element is a reference to the prefix rule
        if(prefixExpr is StormReferenceExpr prefixRef)
        {
            var firstElement = armElements[0];
            if(firstElement is StormLabeledExpr labeled)
            {
                firstElement = labeled.Element;
            }

            if(firstElement is StormReferenceExpr firstRef && firstRef.Name == prefixRef.Name)
            {
                return 1;
            }

            // Case 2: The arm inlines the prefix elements structurally
            var prefixDecl = grammar.LookupByName(prefixRef.Name);
            if(prefixDecl is StormRuleDecl prefixRuleDecl)
            {
                var prefixElements = prefixRuleDecl.Body is StormSequenceExpr prefixSeq
                    ? prefixSeq.Elements.Count
                    : 1;

                if(armElements.Count >= prefixElements)
                {
                    return prefixElements;
                }
            }
        }

        return 1;
    }

    /// <summary>
    /// Gets the tail expression of a rule body — the body with the first N elements removed,
    /// where N is the prefix length. Used by inline block analysis to strip the shared prefix.
    /// If the body is a sequence, returns a new sequence without the first N elements.
    /// If the body has fewer than N elements or is not a sequence, returns an empty sequence.
    /// </summary>
    private static StormExpression GetTailExpression(StormExpression body, int prefixLength)
    {
        if(body is StormSequenceExpr seq)
        {
            if(seq.Elements.Count <= prefixLength)
            {
                // Only the prefix elements, no tail
                return new StormSequenceExpr(new List<StormExpression>());
            }

            // Return everything after the prefix elements
            var tailElements = seq.Elements.Skip(prefixLength).ToList();
            if(tailElements.Count == 1)
            {
                return tailElements[0];
            }

            return new StormSequenceExpr(tailElements);
        }

        // The body is a single expression — empty tail
        return new StormSequenceExpr(new List<StormExpression>());
    }

    /// <summary>
    /// Extracts the top-level alternatives from a rule body.
    /// If the body is an alternation, returns its alternatives.
    /// Otherwise returns a single-element list.
    /// </summary>
    internal static IReadOnlyList<StormExpression> GetTopLevelAlternatives(StormExpression body)
    {
        return body is StormAlternationExpr altExpr
            ? altExpr.Alternatives
            : new List<StormExpression> { body };
    }

    /// <summary>
    /// Computes the FIRST set of an expression — the set of token/string-literal/char-class
    /// identifiers that can appear at the start position. Uses string representations
    /// as set elements for comparison.
    /// </summary>
    internal static HashSet<string> ComputeFirstSet(StormExpression expr, StormGrammar grammar)
    {
        var result = new HashSet<string>();
        ComputeFirst(expr, grammar, result, new HashSet<string>());
        return result;
    }

    /// <summary>
    /// Computes FIRST-k sequences for an expression — the set of token sequences
    /// (up to length k) that can appear at the start of a match. Each sequence is
    /// represented as a string of space-separated token identifiers.
    /// </summary>
    internal static HashSet<string> ComputeFirstKSequences(StormExpression expr, StormGrammar grammar, int k)
    {
        var prefixes = ComputePrefixes(expr, grammar, k, new HashSet<string>());
        var result = new HashSet<string>();
        foreach(var prefix in prefixes)
        {
            _ = result.Add(string.Join(" ", prefix));
        }

        return result;
    }

    /// <summary>
    /// Computes all possible token-sequence prefixes of length up to k for an expression.
    /// Returns a set of lists, where each list is a sequence of token identifiers.
    /// </summary>
    private static List<List<string>> ComputePrefixes(
        StormExpression expr,
        StormGrammar grammar,
        int k,
        HashSet<string> visited)
    {
        if(k <= 0)
        {
            return new List<List<string>> { new() };
        }

        switch(expr)
        {
            case StormStringLiteralExpr strLit:
                return new List<List<string>> { new() { "str:" + strLit.Value } };

            case StormCharClassExpr charClass:
                return new List<List<string>> { new() { "cc:" + charClass.Text } };

            case StormReferenceExpr refExpr:
                {
                    var resolved = grammar.LookupByName(refExpr.Name);
                    if(resolved is StormTokenDecl or StormHiddenTokenDecl)
                    {
                        return new List<List<string>> { new() { "tok:" + refExpr.Name } };
                    }

                    if(resolved is StormFragmentDecl)
                    {
                        return new List<List<string>> { new() { "frag:" + refExpr.Name } };
                    }

                    if(resolved is StormRuleDecl ruleDecl && visited.Add(refExpr.Name))
                    {
                        var result = ComputePrefixes(ruleDecl.Body, grammar, k, visited);
                        _ = visited.Remove(refExpr.Name);
                        return result;
                    }

                    return new List<List<string>> { new() };
                }

            case StormQualifiedReferenceExpr qualRef:
                {
                    var qualName = qualRef.Qualifier + "." + qualRef.Name;
                    var qualResolved = grammar.LookupByName(qualName);
                    if(qualResolved is StormTokenDecl or StormHiddenTokenDecl)
                    {
                        return new List<List<string>> { new() { "tok:" + qualName } };
                    }

                    if(qualResolved is StormFragmentDecl)
                    {
                        return new List<List<string>> { new() { "frag:" + qualName } };
                    }

                    if(qualResolved is StormRuleDecl qualRuleDecl && visited.Add(qualName))
                    {
                        var result = ComputePrefixes(qualRuleDecl.Body, grammar, k, visited);
                        _ = visited.Remove(qualName);
                        return result;
                    }

                    return new List<List<string>> { new() };
                }

            case StormSequenceExpr seqExpr:
                return ComputeSequencePrefixes(seqExpr.Elements, grammar, k, visited);

            case StormAlternationExpr altExpr:
                {
                    var result = new List<List<string>>();
                    foreach(var alt in altExpr.Alternatives)
                    {
                        result.AddRange(ComputePrefixes(alt, grammar, k, visited));
                    }

                    return result;
                }

            case StormOptionalExpr optExpr:
                {
                    var result = new List<List<string>> { new() };
                    result.AddRange(ComputePrefixes(optExpr.Inner, grammar, k, visited));
                    return result;
                }

            case StormZeroOrMoreExpr zomExpr:
                {
                    var result = new List<List<string>> { new() };
                    result.AddRange(ComputePrefixes(zomExpr.Inner, grammar, k, visited));
                    return result;
                }

            case StormOneOrMoreExpr oomExpr:
                return ComputePrefixes(oomExpr.Inner, grammar, k, visited);

            case StormGroupExpr groupExpr:
                return ComputePrefixes(groupExpr.Inner, grammar, k, visited);

            case StormLabeledExpr labeledExpr:
                return ComputePrefixes(labeledExpr.Element, grammar, k, visited);

            case StormNamedAlternative namedAlt:
                return ComputePrefixes(namedAlt.Expression, grammar, k, visited);

            case StormInlineBlockExpr inlineBlock:
                // Opaque treatment: use the FIRST-k set of the prefix rule
                return ComputePrefixes(inlineBlock.Prefix, grammar, k, visited);

            default:
                return new List<List<string>> { new() };
        }
    }

    /// <summary>
    /// Computes prefixes for a sequence of elements by concatenating the prefixes
    /// of each element in order, respecting nullable elements.
    /// </summary>
    private static List<List<string>> ComputeSequencePrefixes(
        IReadOnlyList<StormExpression> elements,
        StormGrammar grammar,
        int k,
        HashSet<string> visited)
    {
        // Start with a single empty prefix
        var current = new List<List<string>> { new() };

        foreach(var element in elements)
        {
            // If all current prefixes have reached length k, stop
            if(current.All(p => p.Count >= k))
            {
                break;
            }

            var next = new List<List<string>>();

            foreach(var prefix in current)
            {
                if(prefix.Count >= k)
                {
                    // This prefix is already at max length, keep it as-is
                    next.Add(prefix);
                    continue;
                }

                var remaining = k - prefix.Count;
                var elementPrefixes = ComputePrefixes(element, grammar, remaining, visited);

                foreach(var ep in elementPrefixes)
                {
                    var combined = new List<string>(prefix);
                    combined.AddRange(ep);
                    // Truncate to k
                    if(combined.Count > k)
                    {
                        combined = combined.Take(k).ToList();
                    }

                    next.Add(combined);
                }
            }

            current = next;

            // If the element is not nullable, we don't continue past it
            // (the nullable case is already handled by ComputePrefixes returning
            // empty-list prefixes for optional/zero-or-more elements)
        }

        return current;
    }

    private static void ComputeFirst(
        StormExpression expr,
        StormGrammar grammar,
        HashSet<string> result,
        HashSet<string> visited)
    {
        switch(expr)
        {
            case StormStringLiteralExpr strLit:
                _ = result.Add("str:" + strLit.Value);
                break;

            case StormCharClassExpr charClass:
                _ = result.Add("cc:" + charClass.Text);
                break;

            case StormReferenceExpr refExpr:
                var resolved = grammar.LookupByName(refExpr.Name);
                if(resolved is StormTokenDecl or StormHiddenTokenDecl)
                {
                    _ = result.Add("tok:" + refExpr.Name);
                }
                else if(resolved is StormFragmentDecl)
                {
                    _ = result.Add("frag:" + refExpr.Name);
                }
                else if(resolved is StormRuleDecl ruleDecl)
                {
                    if(visited.Add(refExpr.Name))
                    {
                        ComputeFirst(ruleDecl.Body, grammar, result, visited);
                    }
                }

                break;

            case StormQualifiedReferenceExpr qualRef:
                var qualName = qualRef.Qualifier + "." + qualRef.Name;
                var qualResolved = grammar.LookupByName(qualName);
                if(qualResolved is StormTokenDecl or StormHiddenTokenDecl)
                {
                    _ = result.Add("tok:" + qualName);
                }
                else if(qualResolved is StormFragmentDecl)
                {
                    _ = result.Add("frag:" + qualName);
                }
                else if(qualResolved is StormRuleDecl qualRuleDecl)
                {
                    if(visited.Add(qualName))
                    {
                        ComputeFirst(qualRuleDecl.Body, grammar, result, visited);
                    }
                }

                break;

            case StormSequenceExpr seqExpr:
                foreach(var element in seqExpr.Elements)
                {
                    ComputeFirst(element, grammar, result, visited);
                    if(!IsNullable(element))
                    {
                        break;
                    }
                }

                break;

            case StormAlternationExpr altExpr:
                foreach(var alt in altExpr.Alternatives)
                {
                    ComputeFirst(alt, grammar, result, visited);
                }

                break;

            case StormOptionalExpr optExpr:
                ComputeFirst(optExpr.Inner, grammar, result, visited);
                break;

            case StormZeroOrMoreExpr zomExpr:
                ComputeFirst(zomExpr.Inner, grammar, result, visited);
                break;

            case StormOneOrMoreExpr oomExpr:
                ComputeFirst(oomExpr.Inner, grammar, result, visited);
                break;

            case StormGroupExpr groupExpr:
                ComputeFirst(groupExpr.Inner, grammar, result, visited);
                break;

            case StormLabeledExpr labeledExpr:
                ComputeFirst(labeledExpr.Element, grammar, result, visited);
                break;

            case StormNamedAlternative namedAlt:
                ComputeFirst(namedAlt.Expression, grammar, result, visited);
                break;

            case StormInlineBlockExpr inlineBlock:
                // Opaque treatment: use the FIRST set of the prefix rule
                ComputeFirst(inlineBlock.Prefix, grammar, result, visited);
                break;
        }
    }

    /// <summary>
    /// Determines if an expression can match the empty string.
    /// </summary>
    internal static bool IsNullable(StormExpression expr)
    {
        return expr switch
        {
            StormOptionalExpr or StormZeroOrMoreExpr or StormPredicateExpr => true,
            StormGroupExpr groupExpr => IsNullable(groupExpr.Inner),
            StormLabeledExpr labeledExpr => IsNullable(labeledExpr.Element),
            StormNamedAlternative namedAlt => IsNullable(namedAlt.Expression),
            StormSequenceExpr seqExpr => seqExpr.Elements.All(IsNullable),
            StormAlternationExpr altExpr => altExpr.Alternatives.Any(IsNullable),
            _ => false,
        };
    }
}