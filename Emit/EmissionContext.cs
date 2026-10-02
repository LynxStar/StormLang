using System;
using System.Collections.Generic;
using Storm.SourceGenerator.Analyze;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Emit;

/// <summary>
/// Holds shared state for all emitters: the consumer namespace, grammar name,
/// and the lowered <see cref="GrammarIR"/>.
/// </summary>
public sealed class EmissionContext
{
    /// <summary>
    /// The namespace under which generated code is placed.
    /// For example, "Example.Generated" for a consumer.
    /// </summary>
    public string ConsumerNamespace { get; }

    /// <summary>
    /// The grammar name, typically derived from the root Storm file name.
    /// Used to name the generated lexer and parser classes (e.g., "Example" → "ExampleLexer", "ExampleParser").
    /// </summary>
    public string GrammarName { get; }

    /// <summary>
    /// The lowered grammar IR that all emitters read from.
    /// </summary>
    public GrammarIR Grammar { get; }

    /// <summary>
    /// The set of rules selected for packrat memoization, with a stable integer id
    /// per memoized rule. Never null; defaults to <see cref="MemoizationPlan.Empty"/>
    /// when memoization is disabled, so emitters constructed without a plan produce
    /// output identical to the pre-feature parser. Only <c>ParserEmitter</c> reads it.
    /// </summary>
    public MemoizationPlan Memoization { get; }

    /// <summary>
    /// All trait declarations in the grammar, lowered to IR.
    /// Empty when no traits are declared.
    /// </summary>
    public IReadOnlyList<TraitIR> Traits { get; }

    /// <summary>
    /// Maps rule names to their bound traits (resolved, including propagated inline block traits).
    /// Empty when no rules bind traits.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<TraitIR>> RuleTraitBindings { get; }

    /// <summary>
    /// When true, the AstEmitter emits [Obsolete] shim properties that forward
    /// to the new Group_Struct data. This enables incremental migration.
    /// </summary>
    public bool EmitObsoleteShims { get; }

    public EmissionContext(
        string consumerNamespace,
        string grammarName,
        GrammarIR grammar,
        MemoizationPlan? memoization = null,
        IReadOnlyList<TraitIR>? traits = null,
        IReadOnlyDictionary<string, IReadOnlyList<TraitIR>>? ruleTraitBindings = null,
        bool emitObsoleteShims = true)
    {
        ConsumerNamespace = consumerNamespace;
        GrammarName = grammarName;
        Grammar = grammar;
        Memoization = memoization ?? MemoizationPlan.Empty;
        Traits = traits ?? Array.Empty<TraitIR>();
        RuleTraitBindings = ruleTraitBindings
            ?? new Dictionary<string, IReadOnlyList<TraitIR>>();
        EmitObsoleteShims = emitObsoleteShims;
    }
}