using System.Collections.Generic;

namespace Storm.SourceGenerator.IR;

/// <summary>
/// Lowered representation of a rule declaration.
/// Holds the rule name and its list of alternatives.
/// </summary>
public sealed class RuleIR
{
    /// <summary>
    /// The rule name as declared in the grammar.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The alternatives for this rule. Each alternative may have an
    /// optional <c>as</c> name, field labels, and a lowered expression.
    /// </summary>
    public IReadOnlyList<RuleAlternativeIR> Alternatives { get; }

    /// <summary>
    /// Whether this rule was annotated with <c>@memo</c> in the source grammar.
    /// </summary>
    public bool IsMemo { get; }

    public RuleIR(string name, IReadOnlyList<RuleAlternativeIR> alternatives, bool isMemo = false)
    {
        Name = name;
        Alternatives = alternatives;
        IsMemo = isMemo;
    }
}

/// <summary>
/// A single alternative within a <see cref="RuleIR"/>.
/// </summary>
public sealed class RuleAlternativeIR
{
    /// <summary>
    /// The optional <c>as Name</c> label for this alternative.
    /// Null if the alternative is not named.
    /// </summary>
    public string? AlternativeName { get; }

    /// <summary>
    /// Field labels declared in this alternative.
    /// Maps label name to the labeled sub-expression.
    /// </summary>
    public IReadOnlyList<FieldLabelIR> FieldLabels { get; }

    /// <summary>
    /// The lowered expression for this alternative.
    /// </summary>
    public ExpressionIR Expression { get; }

    public RuleAlternativeIR(string? alternativeName, IReadOnlyList<FieldLabelIR> fieldLabels, ExpressionIR expression)
    {
        AlternativeName = alternativeName;
        FieldLabels = fieldLabels;
        Expression = expression;
    }
}

/// <summary>
/// A field label within a rule alternative, mapping a label name
/// to its sub-expression.
/// </summary>
public sealed class FieldLabelIR
{
    /// <summary>
    /// The label name (e.g., <c>lhs</c> in <c>lhs:Expr</c>).
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// The lowered expression for the labeled element.
    /// </summary>
    public ExpressionIR Expression { get; }

    public FieldLabelIR(string label, ExpressionIR expression)
    {
        Label = label;
        Expression = expression;
    }
}