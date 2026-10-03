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
}