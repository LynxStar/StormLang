using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Unit coverage for <see cref="AstEmitter.DetectAnonymousGroups"/> (Task 1.4).
/// The rule <c>rule X = a:Y (op:(A | B) right:Y)*;</c> is built directly as IR --
/// no <c>.storm</c> text is parsed -- and the detector is expected to report
/// exactly one anonymous group: the <c>ZeroOrMore</c>-modified inner sequence at
/// index 0 carrying the two labels <c>op</c> and <c>right</c>. The top-level
/// <c>a</c> label sits outside the group and must NOT be reported.
/// </summary>
public class AnonymousGroupDetectionTests
{
    [Fact]
    public void DetectAnonymousGroups_ZeroOrMoreGroup_ReturnsSingleGroupWithOpAndRightLabels()
    {
        // Rule Y is referenced twice (a:Y and right:Y). Each reference MUST be a
        // distinct RuleRefIR instance: the detector links labels to groups by
        // reference identity, so sharing one instance would leak the top-level `a`
        // label into the inner group's label set.
        var ruleY = new RuleIR("Y", new List<RuleAlternativeIR>());

        // Top-level element: a:Y
        var aExpression = new RuleRefIR(ruleY);

        // Inner group body: op:(A | B) right:Y
        var tokenA = new TokenIR("A", new StringLiteralIR("A"), isHidden: false, declarationIndex: 0);
        var tokenB = new TokenIR("B", new StringLiteralIR("B"), isHidden: false, declarationIndex: 1);
        var opExpression = new AlternationIR(new List<ExpressionIR>
        {
            new TokenRefIR(tokenA),
            new TokenRefIR(tokenB),
        });
        var rightExpression = new RuleRefIR(ruleY);

        var innerSequence = new SequenceIR(new List<ExpressionIR>
        {
            opExpression,
            rightExpression,
        });

        // (op:(A | B) right:Y)*
        var group = new ZeroOrMoreIR(innerSequence);

        // Whole alternative expression: a:Y (op:(A | B) right:Y)*
        var topLevelSequence = new SequenceIR(new List<ExpressionIR>
        {
            aExpression,
            group,
        });

        // Field labels reference the exact same expression instances wired into the
        // tree above, matching how the lowering stage links labels to expressions.
        var labelA = new FieldLabelIR("a", aExpression);
        var labelOp = new FieldLabelIR("op", opExpression);
        var labelRight = new FieldLabelIR("right", rightExpression);

        var alternative = new RuleAlternativeIR(
            alternativeName: null,
            fieldLabels: new List<FieldLabelIR>
            {
                labelA,
                labelOp,
                labelRight,
            },
            expression: topLevelSequence);

        var groups = AstEmitter.DetectAnonymousGroups(alternative);

        var detected = Assert.Single(groups);
        Assert.Equal(0, detected.GroupIndex);
        Assert.Equal(GroupModifier.ZeroOrMore, detected.Modifier);
        Assert.Equal(2, detected.Labels.Count);
        Assert.Equal("op", detected.Labels[0].Label);
        Assert.Equal("right", detected.Labels[1].Label);
        Assert.DoesNotContain(detected.Labels, label => label.Label == "a");
    }
}