using System;
using System.Collections.Generic;
using System.Linq;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;

namespace Storm.SourceGenerator.Resolve;

/// <summary>
/// Renames every name a declaration uses to the effective grammar's name for what it means in the declaring file, so
/// later passes need no file context. A name meaning nothing there is reported here when the file was imported under a
/// namespace: left as written, it would find the root's declaration of that name. The root's own unresolved names are
/// left for the validation passes that report them.
/// </summary>
internal sealed class DeclarationRewriter
{
    private readonly ImportScope scope;
    private readonly Func<string, ImportScope> scopeOf;
    private readonly IReadOnlyDictionary<StormDeclaration, string> names;
    private readonly List<StormDiagnostic>? unresolved;
    private StormDeclaration owner = null!;
    private string ownerName = "";
    private string ownerPath = "";

    internal DeclarationRewriter(
        ImportScope scope,
        Func<string, ImportScope> scopeOf,
        IReadOnlyDictionary<StormDeclaration, string> names,
        List<StormDiagnostic>? unresolved)
    {
        this.scope = scope;
        this.scopeOf = scopeOf;
        this.names = names;
        this.unresolved = unresolved;
    }

    internal StormDeclaration Rewrite(StormDeclaration declaration, string name, string filePath)
    {
        owner = declaration;
        ownerName = name;
        ownerPath = filePath;
        return declaration switch
        {
            StormRuleDecl rule => Rule(rule, name),
            StormTokenDecl token => Token(token, name),
            StormHiddenTokenDecl hidden => HiddenToken(hidden, name),
            StormFragmentDecl fragment => Fragment(fragment, name),
            StormTraitDecl trait => Trait(trait, name),
            _ => declaration,
        };
    }

    private StormRuleDecl Rule(StormRuleDecl rule, string name)
    {
        var body = Expression(rule.Body);
        var bindings = Names(rule.TraitBindings);
        return name == rule.Name && ReferenceEquals(body, rule.Body) && ReferenceEquals(bindings, rule.TraitBindings)
            ? rule
            : rule with { Name = name, Body = body, TraitBindings = bindings };
    }

    private StormTokenDecl Token(StormTokenDecl token, string name)
    {
        var body = Expression(token.Body);
        var action = Action(token.Action);
        return name == token.Name && ReferenceEquals(body, token.Body) && ReferenceEquals(action, token.Action)
            ? token
            : token with { Name = name, Body = body, Action = action };
    }

    private StormHiddenTokenDecl HiddenToken(StormHiddenTokenDecl token, string name)
    {
        var body = Expression(token.Body);
        var action = Action(token.Action);
        return name == token.Name && ReferenceEquals(body, token.Body) && ReferenceEquals(action, token.Action)
            ? token
            : token with { Name = name, Body = body, Action = action };
    }

    private StormFragmentDecl Fragment(StormFragmentDecl fragment, string name)
    {
        var body = Expression(fragment.Body);
        return name == fragment.Name && ReferenceEquals(body, fragment.Body) ? fragment : fragment with { Name = name, Body = body };
    }

    private StormTraitDecl Trait(StormTraitDecl trait, string name)
    {
        var fields = trait.Fields.Select(field => Name(field.TypeName) is var type && type == field.TypeName ? field : field with { TypeName = type }).ToList();
        var composed = Names(trait.ComposedTraits)!;
        return name == trait.Name && fields.Zip(trait.Fields, ReferenceEquals).All(same => same) && ReferenceEquals(composed, trait.ComposedTraits)
            ? trait
            : trait with { Name = name, Body = new StormReferenceExpr(name), Fields = fields, ComposedTraits = composed };
    }

    private StormModeAction? Action(StormModeAction? action)
    {
        if(action is not StormPushAction push || push.ModeName == "default")
        {
            return action;
        }

        if(Find(push.ModeName) is not { } mode)
        {
            Report(StormDiagnosticIds.STORM_UNDEFINED_MODE, $"Push action references undefined mode '{push.ModeName}'");
            return action;
        }

        return names[mode] == push.ModeName ? push : new StormPushAction(names[mode]);
    }

    private IReadOnlyList<string>? Names(IReadOnlyList<string>? written)
    {
        if(written == null)
        {
            return null;
        }

        var renamed = written.Select(Name).ToList();
        return renamed.SequenceEqual(written, StringComparer.Ordinal) ? written : renamed;
    }

    private string Name(string written)
    {
        var dot = written.IndexOf('.');
        var target = dot < 0 ? Find(written) : Find(written.Substring(0, dot), written.Substring(dot + 1));
        if(target == null)
        {
            Report(StormDiagnosticIds.STORM_UNDEFINED_REFERENCE, $"Undefined reference '{written}' in declaration '{ownerName}'");
            return written;
        }

        return names[target];
    }

    private StormExpression Expression(StormExpression expression)
    {
        switch(expression)
        {
            case StormReferenceExpr reference:
                return Reference(reference, reference.Name, Find(reference.Name), $"Undefined reference '{reference.Name}'");

            case StormQualifiedReferenceExpr qualified:
                var spelled = qualified.Qualifier + "." + qualified.Name;
                return Reference(qualified, spelled, Find(qualified.Qualifier, qualified.Name), $"Undefined qualified reference '{spelled}'");

            case StormSequenceExpr sequence:
                var elements = Expressions(sequence.Elements);
                return ReferenceEquals(elements, sequence.Elements) ? sequence : new StormSequenceExpr(elements);

            case StormAlternationExpr alternation:
                var alternatives = Expressions(alternation.Alternatives);
                return ReferenceEquals(alternatives, alternation.Alternatives) ? alternation : new StormAlternationExpr(alternatives);

            case StormOptionalExpr optional:
                return Expression(optional.Inner) is var optionalInner && ReferenceEquals(optionalInner, optional.Inner) ? optional : new StormOptionalExpr(optionalInner);

            case StormZeroOrMoreExpr zeroOrMore:
                return Expression(zeroOrMore.Inner) is var zeroOrMoreInner && ReferenceEquals(zeroOrMoreInner, zeroOrMore.Inner) ? zeroOrMore : new StormZeroOrMoreExpr(zeroOrMoreInner);

            case StormOneOrMoreExpr oneOrMore:
                return Expression(oneOrMore.Inner) is var oneOrMoreInner && ReferenceEquals(oneOrMoreInner, oneOrMore.Inner) ? oneOrMore : new StormOneOrMoreExpr(oneOrMoreInner);

            case StormGroupExpr group:
                return Expression(group.Inner) is var groupInner && ReferenceEquals(groupInner, group.Inner) ? group : new StormGroupExpr(groupInner);

            case StormPredicateExpr predicate:
                return Expression(predicate.Inner) is var guarded && ReferenceEquals(guarded, predicate.Inner) ? predicate : predicate with { Inner = guarded };

            case StormLabeledExpr labeled:
                return Expression(labeled.Element) is var element && ReferenceEquals(element, labeled.Element) ? labeled : labeled with { Element = element };

            case StormNamedAlternative named:
                var namedBody = Expression(named.Expression);
                var bindings = Names(named.TraitBindings);
                return ReferenceEquals(namedBody, named.Expression) && ReferenceEquals(bindings, named.TraitBindings)
                    ? named
                    : named with { Expression = namedBody, TraitBindings = bindings };

            case StormInlineBlockExpr block:
                var prefix = Expression(block.Prefix);
                var arms = block.Arms.Select(arm => Expression(arm.Expression) is var armBody && ReferenceEquals(armBody, arm.Expression) ? arm : arm with { Expression = armBody }).ToList();
                return ReferenceEquals(prefix, block.Prefix) && arms.Zip(block.Arms, ReferenceEquals).All(same => same)
                    ? block
                    : block with { Prefix = prefix, Arms = arms };

            default:
                return expression;
        }
    }

    private IReadOnlyList<StormExpression> Expressions(IReadOnlyList<StormExpression> written)
    {
        var rewritten = written.Select(Expression).ToList();
        return rewritten.Zip(written, ReferenceEquals).All(same => same) ? written : rewritten;
    }

    private StormExpression Reference(StormExpression written, string spelled, StormDeclaration? target, string undefined)
    {
        if(target == null)
        {
            Report(StormDiagnosticIds.STORM_UNDEFINED_REFERENCE, $"{undefined} in declaration '{ownerName}'");
            return written;
        }

        var name = names[target];
        return written is StormReferenceExpr && name == spelled ? written : new StormReferenceExpr(name);
    }

    private StormDeclaration? Find(string name)
    {
        return scope.Find(name);
    }

    private StormDeclaration? Find(string alias, string name)
    {
        return scope.Aliases
            .Where(imported => imported.Alias == alias)
            .Select(imported => scopeOf(imported.Path).Find(name))
            .FirstOrDefault(found => found != null);
    }

    private void Report(string id, string message)
    {
        unresolved?.Add(new StormDiagnostic(id, StormDiagnosticSeverity.Error, message, ownerPath, owner.Line, owner.Column));
    }
}