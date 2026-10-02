using System.Collections.Generic;

namespace Storm.SourceGenerator.Frontend;

// -----------------------------------------------------------------------
//  Top-level file
// -----------------------------------------------------------------------

/// <summary>
/// Represents a complete Storm grammar file: an optional grammar declaration,
/// a sequence of imports, followed by a sequence of declarations.
/// </summary>
public sealed record StormFile(
    StormGrammarDecl? GrammarDeclaration,
    IReadOnlyList<StormImport> Imports,
    IReadOnlyList<StormDeclaration> Declarations);

// -----------------------------------------------------------------------
//  Grammar declaration
// -----------------------------------------------------------------------

/// <summary>
/// A <c>grammar Name;</c> declaration that identifies the root grammar file
/// and provides the grammar name.
/// </summary>
public sealed record StormGrammarDecl(
    string Name,
    int Offset,
    int Line,
    int Column);

// -----------------------------------------------------------------------
//  Imports
// -----------------------------------------------------------------------

/// <summary>
/// An <c>import "path";</c> or <c>import "path" as Namespace;</c> declaration.
/// </summary>
public sealed record StormImport(
    string Path,
    string? Alias,
    int Offset,
    int Line,
    int Column);

// -----------------------------------------------------------------------
//  Declarations
// -----------------------------------------------------------------------

/// <summary>
/// Base type for all top-level Storm declarations
/// (<c>rule</c>, <c>token</c>, <c>fragment</c>, <c>hidden token</c>).
/// </summary>
public abstract record StormDeclaration(
    string Name,
    StormExpression Body,
    int Offset,
    int Line,
    int Column);

/// <summary>A <c>rule Name = ...;</c> declaration, optionally annotated with <c>@memo</c>.</summary>
public sealed record StormRuleDecl(
    string Name,
    StormExpression Body,
    int Offset,
    int Line,
    int Column,
    bool IsMemo = false,
    IReadOnlyList<string>? TraitBindings = null)
    : StormDeclaration(Name, Body, Offset, Line, Column);

/// <summary>A <c>token Name = ...;</c> declaration.</summary>
public sealed record StormTokenDecl(
    string Name,
    StormExpression Body,
    int Offset,
    int Line,
    int Column,
    StormModeAction? Action = null)
    : StormDeclaration(Name, Body, Offset, Line, Column);

/// <summary>A <c>fragment Name = ...;</c> declaration.</summary>
public sealed record StormFragmentDecl(
    string Name,
    StormExpression Body,
    int Offset,
    int Line,
    int Column)
    : StormDeclaration(Name, Body, Offset, Line, Column);

/// <summary>A <c>hidden token Name = ...;</c> declaration.</summary>
public sealed record StormHiddenTokenDecl(
    string Name,
    StormExpression Body,
    int Offset,
    int Line,
    int Column,
    StormModeAction? Action = null)
    : StormDeclaration(Name, Body, Offset, Line, Column);

// -----------------------------------------------------------------------
//  Mode actions
// -----------------------------------------------------------------------

/// <summary>
/// Base type for mode transition actions on token declarations (<c>=> push</c> / <c>=> pop</c>).
/// </summary>
public abstract record StormModeAction;

/// <summary>A push action: <c>=> push ModeName</c></summary>
public sealed record StormPushAction(string ModeName) : StormModeAction;

/// <summary>A pop action: <c>=> pop</c></summary>
public sealed record StormPopAction() : StormModeAction;

// -----------------------------------------------------------------------
//  Mode declarations
// -----------------------------------------------------------------------

/// <summary>
/// A mode block declaration: <c>mode ModeName { declarations }</c>.
/// The <see cref="StormDeclaration.Body"/> is set to a placeholder
/// <see cref="StormReferenceExpr"/> since mode declarations don't have a body expression.
/// </summary>
public sealed record StormModeDecl(
    string Name,
    IReadOnlyList<StormDeclaration> Declarations,
    int Offset,
    int Line,
    int Column)
    : StormDeclaration(Name, new StormReferenceExpr(Name), Offset, Line, Column);

// -----------------------------------------------------------------------
//  Trait declarations
// -----------------------------------------------------------------------

/// <summary>
/// The cardinality of a trait field.
/// </summary>
public enum StormFieldCardinality
{
    /// <summary>Exactly one (no suffix).</summary>
    One,

    /// <summary>Optional (<c>?</c>).</summary>
    Optional,

    /// <summary>Zero or more (<c>*</c>).</summary>
    ZeroOrMore,

    /// <summary>One or more (<c>+</c>).</summary>
    OneOrMore,
}

/// <summary>
/// A single field within a trait declaration: <c>fieldName:TypeName</c> with optional
/// cardinality suffix (<c>?</c>, <c>*</c>, <c>+</c>).
/// </summary>
public sealed record StormTraitField(
    string FieldName,
    string TypeName,
    StormFieldCardinality Cardinality);

/// <summary>
/// A <c>trait Name = field1:Type1 field2:Type2*;</c> declaration.
/// Traits may optionally compose other traits via colon syntax:
/// <c>trait B : A = extraField:Foo;</c>
/// The <see cref="StormDeclaration.Body"/> is set to a placeholder
/// <see cref="StormReferenceExpr"/> since trait declarations don't have a body expression.
/// </summary>
public sealed record StormTraitDecl(
    string Name,
    IReadOnlyList<StormTraitField> Fields,
    IReadOnlyList<string> ComposedTraits,
    int Offset,
    int Line,
    int Column)
    : StormDeclaration(Name, new StormReferenceExpr(Name), Offset, Line, Column);

// -----------------------------------------------------------------------
//  Expressions
// -----------------------------------------------------------------------

/// <summary>
/// Base type for all Storm expression nodes used in rule/token/fragment bodies.
/// </summary>
public abstract record StormExpression;

/// <summary>
/// A sequence of two or more expressions (juxtaposition).
/// <c>A B C</c> → <c>StormSequenceExpr([A, B, C])</c>
/// </summary>
public sealed record StormSequenceExpr(
    IReadOnlyList<StormExpression> Elements)
    : StormExpression;

/// <summary>
/// An alternation of two or more expressions.
/// <c>A | B | C</c> → <c>StormAlternationExpr([A, B, C])</c>
/// </summary>
public sealed record StormAlternationExpr(
    IReadOnlyList<StormExpression> Alternatives)
    : StormExpression;

/// <summary>
/// An optional expression: <c>E?</c>
/// </summary>
public sealed record StormOptionalExpr(
    StormExpression Inner)
    : StormExpression;

/// <summary>
/// A zero-or-more repetition: <c>E*</c>
/// </summary>
public sealed record StormZeroOrMoreExpr(
    StormExpression Inner)
    : StormExpression;

/// <summary>
/// A one-or-more repetition: <c>E+</c>
/// </summary>
public sealed record StormOneOrMoreExpr(
    StormExpression Inner)
    : StormExpression;

/// <summary>
/// A parenthesized group: <c>( E )</c>
/// </summary>
public sealed record StormGroupExpr(
    StormExpression Inner)
    : StormExpression;

/// <summary>
/// A syntactic predicate, an ampersand or a bang before one element: the first succeeds where the element
/// matches and the second where it does not. Neither consumes input nor contributes to the AST, which is what
/// lets an alternative be chosen by what follows it.
/// </summary>
public sealed record StormPredicateExpr(
    StormExpression Inner,
    bool IsNegated)
    : StormExpression;

/// <summary>
/// A double-quoted string literal: <c>"fn"</c>
/// </summary>
public sealed record StormStringLiteralExpr(
    string Value)
    : StormExpression;

/// <summary>
/// A character class: <c>[a-zA-Z_]</c>, <c>[^"\\]</c>, etc.
/// The <see cref="Text"/> property holds the raw bracket-delimited text.
/// </summary>
public sealed record StormCharClassExpr(
    string Text)
    : StormExpression;

/// <summary>
/// A backtick-delimited regex literal: <c>`[a-zA-Z]+`</c>
/// </summary>
public sealed record StormRegexLiteralExpr(
    string Pattern)
    : StormExpression;

/// <summary>
/// A simple (unqualified) reference to a rule, token, or fragment: <c>Identifier</c>
/// </summary>
public sealed record StormReferenceExpr(
    string Name)
    : StormExpression;

/// <summary>
/// A qualified reference: <c>Namespace.Name</c>
/// </summary>
public sealed record StormQualifiedReferenceExpr(
    string Qualifier,
    string Name)
    : StormExpression;

/// <summary>
/// A field-labeled expression: <c>label:Element</c>
/// </summary>
public sealed record StormLabeledExpr(
    string Label,
    StormExpression Element)
    : StormExpression;

/// <summary>
/// A named alternative: an expression followed by <c>as Name</c>.
/// Used within rule alternations to name AST subtypes.
/// Optionally binds traits: <c>as Name : Trait1, Trait2</c>.
/// </summary>
public sealed record StormNamedAlternative(
    StormExpression Expression,
    string AlternativeName,
    IReadOnlyList<string>? TraitBindings = null)
    : StormExpression;

/// <summary>
/// An inline block expression: <c>inline PrefixRule: { arm, arm, ... }</c>
/// Represents a set of alternatives that share a common prefix.
/// </summary>
public sealed record StormInlineBlockExpr(
    StormExpression Prefix,
    IReadOnlyList<StormInlineArm> Arms,
    int Offset,
    int Line,
    int Column) : StormExpression;

// -----------------------------------------------------------------------
//  Inline arms
// -----------------------------------------------------------------------

/// <summary>
/// A single arm within an inline block: <c>RuleRef as AltName</c> or
/// <c>label:RuleRef as AltName</c>.
/// </summary>
public sealed record StormInlineArm(
    StormExpression Expression,
    string AlternativeName,
    string? Label);