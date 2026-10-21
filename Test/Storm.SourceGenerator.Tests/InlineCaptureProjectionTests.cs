using System.Text.RegularExpressions;
using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Tests;

[Trait("Tier", "2")]
public sealed class InlineCaptureProjectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StructuralPrefixModifiersFillTheArmsRelabelledCapture(bool repeated)
    {
        var ident = new TokenIR("IDENT", new StringLiteralIR("x"), false, 0);
        var semi = new TokenIR("SEMI", new StringLiteralIR(";"), false, 1);
        var elem = new RuleIR("Elem", [new RuleAlternativeIR(null, [], new TokenRefIR(ident))]);
        var prefixCapture = modified(new RuleRefIR(elem));
        var targetCapture = modified(new RuleRefIR(elem));
        var parsedCapture = modified(new RuleRefIR(elem));
        var prefix = new RuleIR("Prefix", [new RuleAlternativeIR(null, [new FieldLabelIR("a", prefixCapture)],
            new SequenceIR([prefixCapture, new TokenRefIR(semi)]))]);
        var targetTail = new RuleRefIR(elem);
        var target = new RuleIR("Relabel", [new RuleAlternativeIR(null,
            [new FieldLabelIR("b", targetCapture), new FieldLabelIR("tail", targetTail)],
            new SequenceIR([targetCapture, new TokenRefIR(semi), targetTail]))]);
        var parsedTail = new RuleRefIR(elem);
        var block = new InlineBlockIR(prefix, [parsedCapture, new TokenRefIR(semi)], [new FieldLabelIR("a", parsedCapture)],
            [new InlineArmIR("RelabelArm", "relabel", target, parsedTail, [new FieldLabelIR("tail", parsedTail)])]);
        var pick = new RuleIR("Pick", [new RuleAlternativeIR(null, [], block)]);
        var grammar = new GrammarIR([ident, semi], [elem, prefix, target, pick]);

        var parser = ParserEmitter.Emit(new EmissionContext("Test.Generated", "Test", grammar, emitObsoleteShims: false));

        Assert.Contains("_inner.B =", parser);
        Assert.Contains("_inner.Tail =", parser);
        Assert.DoesNotContain("_inner.A =", parser);
        Assert.DoesNotContain("_prefixNode", parser);

        ExpressionIR modified(ExpressionIR reference)
        {
            return repeated ? new ZeroOrMoreIR(reference) : new OptionalIR(reference);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NominalPrefixGroupsRemainOwnedByTheCapturedPrefix(bool repeated)
    {
        var ident = new TokenIR("IDENT", new StringLiteralIR("x"), false, 0);
        var semi = new TokenIR("SEMI", new StringLiteralIR(";"), false, 1);
        var elem = new RuleIR("Elem", [new RuleAlternativeIR(null, [], new TokenRefIR(ident))]);
        var prefixLabels = new List<FieldLabelIR>();
        var parsedLabels = new List<FieldLabelIR>();
        var prefixGroup = group("a", "b", prefixLabels);
        var parsedGroup = group("a", "b", parsedLabels);
        var prefix = new RuleIR("Prefix", [new RuleAlternativeIR(null, prefixLabels, new SequenceIR([prefixGroup, new TokenRefIR(semi)]))]);
        var targetTail = new RuleRefIR(elem);
        var prefixReference = new RuleRefIR(prefix);
        var target = new RuleIR("Direct", [new RuleAlternativeIR(null,
            [new FieldLabelIR("head", prefixReference), new FieldLabelIR("tail", targetTail)],
            new SequenceIR([prefixReference, targetTail]))]);
        var parsedTail = new RuleRefIR(elem);
        var block = new InlineBlockIR(prefix, [parsedGroup, new TokenRefIR(semi)], parsedLabels,
            [new InlineArmIR("DirectArm", "direct", target, parsedTail, [new FieldLabelIR("tail", parsedTail)])]);
        var pick = new RuleIR("Pick", [new RuleAlternativeIR(null, [], block)]);
        var grammar = new GrammarIR([ident, semi], [elem, prefix, target, pick]);

        var parser = ParserEmitter.Emit(new EmissionContext("Test.Generated", "Test", grammar, emitObsoleteShims: false));

        Assert.Contains("new PrefixNodeGroup0 { A =", parser);
        Assert.Contains(", B =", parser);
        Assert.Contains("_prefixNode.Group0 =", parser);
        Assert.Contains("_inner.Head = _prefixNode;", parser);
        Assert.Contains("_prefixNode.Span = _inlinePrefixSpan;", parser);
        Assert.DoesNotContain("_inner.Group0 =", parser);
        if(!repeated)
        {
            Assert.Matches(@"if \(_inlineGroup0Ok && !_hasError\)\s*\{\s*_group0 = new PrefixNodeGroup0", parser);
        }

        ExpressionIR group(string first, string second, List<FieldLabelIR> labels)
        {
            var firstReference = new RuleRefIR(elem);
            var secondReference = new RuleRefIR(elem);
            labels.Add(new FieldLabelIR(first, firstReference));
            labels.Add(new FieldLabelIR(second, secondReference));
            var sequence = new SequenceIR([firstReference, secondReference]);
            return repeated ? new ZeroOrMoreIR(sequence) : new OptionalIR(sequence);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANestedBlockParsesOnlyWhatItsPrefixAddsAndBuildsFlatArmNodes(bool expanded)
    {
        var a = new TokenIR("A", new StringLiteralIR("a"), false, 0);
        var b = new TokenIR("B", new StringLiteralIR("b"), false, 1);
        var c = new TokenIR("C", new StringLiteralIR("c"), false, 2);
        var d = new TokenIR("D", new StringLiteralIR("d"), false, 3);
        var outerA = new TokenRefIR(a);
        var outer = new RuleIR("Outer", [new RuleAlternativeIR(null, [new FieldLabelIR("a", outerA)], outerA)]);
        var inner = new RuleIR("Inner", [sequence(expanded ? [("x", new TokenRefIR(a)), ("b", new TokenRefIR(b))] : [("a", new RuleRefIR(outer)), ("b", new TokenRefIR(b))])]);
        var leaf = new RuleIR("Leaf", [sequence(expanded
            ? [("p", new TokenRefIR(a)), ("q", new TokenRefIR(b)), ("c", new TokenRefIR(c))]
            : [("b", new RuleRefIR(inner)), ("c", new TokenRefIR(c))])]);
        var other = new RuleIR("Other", [sequence([("a", new RuleRefIR(outer)), ("d", new TokenRefIR(d))])]);
        var nestedPrefix = expanded ? [("x", new TokenRefIR(a)), ("b", new TokenRefIR(b))] : new (string, ExpressionIR)[] { ("a", new RuleRefIR(outer)), ("b", new TokenRefIR(b)) };
        var leafTail = new TokenRefIR(c);
        var nested = new InlineBlockIR(inner, nestedPrefix.Select(element => element.Item2).ToList(), nestedPrefix.Select(element => new FieldLabelIR(element.Item1, element.Item2)).ToList(),
            [new InlineArmIR("LeafArm", "leaf", leaf, leafTail, [new FieldLabelIR("c", leafTail)])]);
        var parsedA = new TokenRefIR(a);
        var otherTail = new TokenRefIR(d);
        var block = new InlineBlockIR(outer, [parsedA], [new FieldLabelIR("a", parsedA)],
            [new InlineArmIR("NestedArm", "inner", inner, nested, []), new InlineArmIR("OtherArm", "other", other, otherTail, [new FieldLabelIR("d", otherTail)])]);
        var parent = new RuleIR("Parent", [new RuleAlternativeIR(null, [], block)]);
        var context = new EmissionContext("Test.Generated", "Test", new GrammarIR([a, b, c, d], [outer, inner, leaf, other, parent]), emitObsoleteShims: false);

        var parser = ParserEmitter.Emit(context);
        var ast = AstEmitter.EmitAstNodes(context);
        var parse = parser[parser.IndexOf("ParentNode? _ParseParent()", StringComparison.Ordinal)..];

        Assert.Empty(ShadowedLocals(parser));
        _ = Assert.Single(Regex.Matches(parse, Regex.Escape("Match((int)TokenKind.A)")));
        Assert.DoesNotContain("_ParseOuter()", parse);
        Assert.Contains("public sealed class LeafArmParentNode : ParentNode", ast);
        Assert.Contains("public sealed class OtherArmParentNode : ParentNode", ast);
        Assert.DoesNotContain("NestedArmParentNode", ast);
        if(expanded)
        {
            Assert.Contains("_inner1.P = _A;", parse);
            Assert.Contains("_inner1.Q = _B;", parse);
            Assert.DoesNotContain("_prefixNode1", parse);
        }
        else
        {
            Assert.Contains("_prefixNode1.A = _prefixNode;", parse);
            Assert.Contains("_inner1.B = _prefixNode1;", parse);
        }

        static RuleAlternativeIR sequence((string Label, ExpressionIR Element)[] elements)
        {
            return new RuleAlternativeIR(null, elements.Select(element => new FieldLabelIR(element.Label, element.Element)).ToList(),
                new SequenceIR(elements.Select(element => element.Element).ToList()));
        }
    }

    private static List<string> ShadowedLocals(string source)
    {
        var declaration = new Regex(@"^(?:var|[A-Z][\w.]*(?:<.*?>)?\??)\s+(\w+)\s*=(?!=)");
        var root = new Scope(null);
        var current = root;
        foreach(var line in source.Split('\n').Select(line => line.Trim()))
        {
            if(line == "{")
            {
                current = new Scope(current);
                current.Parent!.Children.Add(current);
            }
            else if(line == "}")
            {
                current = current.Parent!;
            }
            else if(declaration.Match(line) is { Success: true } match)
            {
                _ = current.Names.Add(match.Groups[1].Value);
            }
        }

        var shadowed = new List<string>();
        walk(root, []);
        return shadowed;

        void walk(Scope scope, HashSet<string> enclosing)
        {
            shadowed.AddRange(scope.Names.Where(enclosing.Contains));
            foreach(var child in scope.Children)
            {
                walk(child, [.. enclosing, .. scope.Names]);
            }
        }
    }

    private sealed class Scope(Scope? parent)
    {
        public Scope? Parent { get; } = parent;

        public List<Scope> Children { get; } = [];

        public HashSet<string> Names { get; } = [];
    }
}