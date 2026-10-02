using System.Collections.Generic;
using Storm.SourceGenerator.Frontend;

namespace Storm.SourceGenerator.IR;

/// <summary>
/// Lowered representation of a trait declaration.
/// Contains resolved field references for emission.
/// </summary>
public sealed class TraitIR
{
    /// <summary>
    /// The trait name as declared in the grammar.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The fields declared directly on this trait.
    /// </summary>
    public IReadOnlyList<TraitFieldIR> Fields { get; }

    /// <summary>
    /// Traits composed into this trait via the <c>:</c> syntax.
    /// </summary>
    public IReadOnlyList<TraitIR> ComposedTraits { get; }

    public TraitIR(string name, IReadOnlyList<TraitFieldIR> fields,
                   IReadOnlyList<TraitIR> composedTraits)
    {
        Name = name;
        Fields = fields;
        ComposedTraits = composedTraits;
    }

    /// <summary>
    /// Returns all fields including those inherited from composed traits,
    /// deduplicated by field name. Own fields take precedence over composed fields.
    /// </summary>
    public IEnumerable<TraitFieldIR> AllFields()
    {
        var seen = new Dictionary<string, TraitFieldIR>();

        CollectAllFieldsRecursive(seen);

        return seen.Values;
    }

    private void CollectAllFieldsRecursive(Dictionary<string, TraitFieldIR> seen)
    {
        foreach(var composed in ComposedTraits)
        {
            composed.CollectAllFieldsRecursive(seen);
        }

        foreach(var field in Fields)
        {
            seen[field.FieldName] = field;
        }
    }
}

/// <summary>
/// A single field within a trait, with its type resolved to either
/// a token or a rule name (string-based, for emission purposes).
/// </summary>
public sealed class TraitFieldIR
{
    /// <summary>
    /// The field label name (e.g., <c>lhs</c> in <c>lhs: Expr</c>).
    /// </summary>
    public string FieldName { get; }

    /// <summary>
    /// The resolved type name (token name or rule name).
    /// </summary>
    public string TypeName { get; }

    /// <summary>
    /// Whether this field references a token (true) or a rule (false).
    /// </summary>
    public bool IsToken { get; }

    /// <summary>
    /// The cardinality of this field (One, Optional, ZeroOrMore, OneOrMore).
    /// </summary>
    public StormFieldCardinality Cardinality { get; }

    public TraitFieldIR(string fieldName, string typeName, bool isToken,
                        StormFieldCardinality cardinality)
    {
        FieldName = fieldName;
        TypeName = typeName;
        IsToken = isToken;
        Cardinality = cardinality;
    }
}