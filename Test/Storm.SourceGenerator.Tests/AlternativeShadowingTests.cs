using System.Text.RegularExpressions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;
using Storm.SourceGenerator.Resolve;
using Storm.SourceGenerator.Validate;

namespace Storm.SourceGenerator.Tests;

public class AlternativeShadowingTests
{
    private const int RuleLine = 40;

    private static readonly string[] Letters = ["A", "B", "C"];

    private sealed record Case(StormExpression[] Alternatives, int[] Flagged, params StormRuleDecl[] Rules);

    private static readonly Dictionary<string, Case> Cases = new()
    {
        ["SharedThreeTokensThenDiverge"] = new([Tokens("A B C D"), Tokens("A B C E")], []),
        ["SharedFiveTokensThenDiverge"] = new([Tokens("A B C D E F"), Tokens("A B C D E G")], []),
        ["Duplicate"] = new([Tokens("A B"), Tokens("A B")], [2]),
        ["DuplicateLongerThanThreeTokens"] = new([Tokens("A B C D E F"), Tokens("A B C D E F")], [2]),
        ["ShorterEarlierWinsOnTheLonger"] = new([Tokens("A B"), Tokens("A B C")], [2]),
        ["LongerEarlierLeavesTheShorter"] = new([Tokens("A B C"), Tokens("A B")], []),
        ["EarlierAlternativesCoverTogether"] = new([Tokens("A C"), Tokens("A D"), Seq(Ref("A"), Or(Ref("C"), Ref("D")))], [3]),
        ["AnOptionalAlternativeAlwaysWins"] = new([Opt(Ref("A")), Ref("B"), Ref("C")], [2, 3]),
        ["AZeroOrMoreAlternativeAlwaysWins"] = new([Star(Ref("A")), Ref("B")], [2]),
        ["ANullableLaterAlternativeMatchesWhereTheEarlierFails"] = new([Ref("A"), Opt(Ref("B"))], []),
        ["AnOptionalTailStillWins"] = new([Seq(Ref("A"), Opt(Ref("B"))), Tokens("A C")], [2]),
        ["AnOptionalMatchesBeforeItsSequenceContinues"] = new([Seq(Ref("A"), Opt(Ref("B")), Tokens("C D E")), Tokens("A B C D F")], []),
        ["AnOptionalThatFailsIsSkipped"] = new([Seq(Ref("A"), Opt(Ref("B")), Ref("C")), Tokens("A C D")], [2]),
        ["ARepetitionIsGreedy"] = new([Seq(Ref("A"), Star(Ref("B")), Ref("B")), Tokens("A B B")], []),
        ["ARepetitionStopsBeforeACompleteMatch"] = new([Seq(Ref("A"), Star(Ref("B")), Ref("C")), Tokens("A B C D")], [2]),
        ["ALaterRepetitionIsUnrolled"] = new([Tokens("A C"), Tokens("A B C"), Seq(Ref("A"), Star(Ref("B")), Ref("C"))], []),
        ["ARepetitionIsCoveredRoundByRound"] = new([Tokens("A C"), Tokens("A B C"), Tokens("A B B"), Seq(Ref("A"), Star(Ref("B")), Ref("C"))], [4]),
        ["AnAlwaysSucceedingElementFollowsAnUnknownEnd"] = new([Seq(Ref("A"), Star(Ref("B")), Opt(Ref("C"))), Tokens("A B")], [2]),
        ["AOneOrMoreNeedsARoundThatConsumes"] = new([Seq(Ref("A"), Plus(Opt(Ref("B")))), Tokens("A C")], []),
        ["ARuleReferenceSharingThreeTokens"] = new([Seq(Ref("Q"), Ref("D")), Tokens("A B C E")], [], Rule("Q", 50, Tokens("A B C"))),
        ["ARuleReferenceDuplicated"] = new([Ref("Q"), Tokens("A B C D E")], [2], Rule("Q", 50, Tokens("A B C D"))),
        ["ARecursionIsNotAMatch"] = new([Seq(Ref("P"), Tokens("B D")), Tokens("A B B D")], [], Rule("P", 50, Or(Seq(Ref("A"), Ref("P"), Ref("B")), Ref("C")))),
        ["ARecursionLeavesItsContinuationUnknown"] = new([Tokens("A B B"), Ref("C"), Seq(Ref("P"), Ref("B"))], [], Rule("P", 50, Or(Seq(Ref("A"), Ref("P"), Ref("B")), Ref("C")))),
        ["AGuardThatFailsDoesNotCover"] = new([Seq(Tokens("A B C"), And(Ref("D"))), Tokens("A B C E")], []),
        ["AGuardTheLaterTokensSatisfyCovers"] = new([Seq(Tokens("A B C"), And(Ref("D"))), Tokens("A B C D")], [2]),
        ["ANegatedGuardBetweenDeclaredTokens"] = new([Seq(Ref("A"), Not(Ref("B"))), Tokens("A C")], [2]),
        ["ALiteralIsNotToldApartFromTheTokenSpellingIt"] = new([Seq(Ref("SEMI"), Not(Lit(";"))), Tokens("SEMI SEMI")], []),
        ["AnOptionalLiteralMayBeTheTokenAfterIt"] = new([Seq(Ref("SEMI"), Opt(Lit(";")), Ref("SEMI")), Tokens("SEMI SEMI")], []),
        ["AChoiceLeftUndecidedHasNoKnownEnd"] = new([Seq(Or(Lit(";"), Tokens("SEMI A")), Ref("B")), Tokens("SEMI A B")], []),
        ["AChoiceLeftUndecidedDoesNotFail"] = new([Seq(Not(Or(Lit(";"), Ref("B"))), Ref("SEMI")), Ref("SEMI")], []),
    };

    [Theory]
    [Trait("Tier", "1")]
    [InlineData("SharedThreeTokensThenDiverge")]
    [InlineData("SharedFiveTokensThenDiverge")]
    [InlineData("Duplicate")]
    [InlineData("DuplicateLongerThanThreeTokens")]
    [InlineData("ShorterEarlierWinsOnTheLonger")]
    [InlineData("LongerEarlierLeavesTheShorter")]
    [InlineData("EarlierAlternativesCoverTogether")]
    [InlineData("AnOptionalAlternativeAlwaysWins")]
    [InlineData("AZeroOrMoreAlternativeAlwaysWins")]
    [InlineData("ANullableLaterAlternativeMatchesWhereTheEarlierFails")]
    [InlineData("AnOptionalTailStillWins")]
    [InlineData("AnOptionalMatchesBeforeItsSequenceContinues")]
    [InlineData("AnOptionalThatFailsIsSkipped")]
    [InlineData("ARepetitionIsGreedy")]
    [InlineData("ARepetitionStopsBeforeACompleteMatch")]
    [InlineData("ALaterRepetitionIsUnrolled")]
    [InlineData("ARepetitionIsCoveredRoundByRound")]
    [InlineData("AnAlwaysSucceedingElementFollowsAnUnknownEnd")]
    [InlineData("AOneOrMoreNeedsARoundThatConsumes")]
    [InlineData("ARuleReferenceSharingThreeTokens")]
    [InlineData("ARuleReferenceDuplicated")]
    [InlineData("ARecursionIsNotAMatch")]
    [InlineData("ARecursionLeavesItsContinuationUnknown")]
    [InlineData("AGuardThatFailsDoesNotCover")]
    [InlineData("AGuardTheLaterTokensSatisfyCovers")]
    [InlineData("ANegatedGuardBetweenDeclaredTokens")]
    [InlineData("ALiteralIsNotToldApartFromTheTokenSpellingIt")]
    [InlineData("AnOptionalLiteralMayBeTheTokenAfterIt")]
    [InlineData("AChoiceLeftUndecidedHasNoKnownEnd")]
    [InlineData("AChoiceLeftUndecidedDoesNotFail")]
    public void AnAlternativeIsReportedOnlyWhereAnEarlierOneWinsOnEveryInputItMatches(string name)
    {
        var shape = Cases[name];

        var diagnostics = ReachabilityPass.Run(Grammar([Rule("R", RuleLine, Or(shape.Alternatives)), .. shape.Rules]));

        Assert.All(diagnostics, diagnostic =>
        {
            Assert.Equal(StormDiagnosticIds.STORM_UNREACHABLE_ALTERNATIVE, diagnostic.Id);
            Assert.Equal(StormDiagnosticSeverity.Warning, diagnostic.Severity);
            Assert.Equal(RuleLine, diagnostic.Line);
        });
        Assert.Equal(shape.Flagged, diagnostics.Select(ReportedAlternative));
    }

    [Property(MaxTest = 300)]
    [Trait("Tier", "1")]
    public Property AReportedAlternativeNeverMatchesAnInputEveryEarlierOneFails()
    {
        var shapes = Gen.Choose(2, 4).SelectMany(count => Gen.ArrayOf(Expression(2), count));

        return Prop.ForAll(shapes.ToArbitrary(), alternatives =>
        {
            var reported = ReachabilityPass.Run(Grammar([Rule("R", RuleLine, Or(alternatives))])).Select(ReportedAlternative);

            foreach(var number in reported)
            {
                var input = Inputs(7).FirstOrDefault(tokens =>
                    Match(alternatives[number - 1], tokens, 0) != null
                    && alternatives.Take(number - 1).All(earlier => Match(earlier, tokens, 0) == null));
                Assert.True(input == null, $"Alternative {number} of {Print(alternatives)} is reached on '{string.Join(" ", input ?? [])}'");
            }
        });
    }

    [Property(MaxTest = 300)]
    [Trait("Tier", "1")]
    public Property AlternativesOfPlainTokensAreReportedExactlyWhenAnEarlierOneIsAPrefix()
    {
        var shapes = Gen.Choose(2, 4).SelectMany(count =>
            Gen.ArrayOf(Gen.Choose(1, 6).SelectMany(length => Gen.ArrayOf(Gen.Elements("A", "B"), length)), count));

        return Prop.ForAll(shapes.ToArbitrary(), sentences =>
        {
            var alternatives = sentences.Select(sentence => Tokens(string.Join(" ", sentence))).ToArray();

            var reported = ReachabilityPass.Run(Grammar([Rule("R", RuleLine, Or(alternatives))])).Select(ReportedAlternative);

            var shadowed = Enumerable.Range(1, alternatives.Length)
                .Where(number => alternatives.Take(number - 1).Any(earlier => Match(earlier, sentences[number - 1], 0) != null));
            Assert.Equal(shadowed, reported);
        });
    }

    private static int ReportedAlternative(StormDiagnostic diagnostic)
    {
        return int.Parse(Regex.Match(diagnostic.Message, "^Alternative (\\d+) in rule 'R' ").Groups[1].Value);
    }

    private static int? Match(StormExpression expression, IReadOnlyList<string> input, int position)
    {
        switch(expression)
        {
            case StormReferenceExpr reference:
                return position < input.Count && input[position] == reference.Name ? position + 1 : null;

            case StormSequenceExpr sequence:
                int? end = position;
                foreach(var element in sequence.Elements)
                {
                    end = end == null ? null : Match(element, input, end.Value);
                }

                return end;

            case StormAlternationExpr alternation:
                return alternation.Alternatives.Select(alternative => Match(alternative, input, position)).FirstOrDefault(match => match != null);

            case StormOptionalExpr optional:
                return Match(optional.Inner, input, position) ?? position;

            case StormZeroOrMoreExpr zeroOrMore:
                return Rounds(zeroOrMore.Inner, input, position);

            case StormOneOrMoreExpr oneOrMore:
                var first = Match(oneOrMore.Inner, input, position);
                return first == null || first == position ? null : Rounds(oneOrMore.Inner, input, first.Value);

            case StormPredicateExpr predicate:
                return Match(predicate.Inner, input, position) == null == predicate.IsNegated ? position : null;

            default:
                throw new ArgumentException(expression.ToString());
        }
    }

    private static int Rounds(StormExpression inner, IReadOnlyList<string> input, int position)
    {
        while(Match(inner, input, position) is { } end && end > position)
        {
            position = end;
        }

        return position;
    }

    private static IEnumerable<string[]> Inputs(int maxLength)
    {
        IEnumerable<string[]> level = [[]];
        for(var length = 0; length <= maxLength; length++)
        {
            foreach(var input in level)
            {
                yield return input;
            }

            level = level.SelectMany(input => Letters.Select(letter => (string[])[.. input, letter])).ToList();
        }
    }

    private static Gen<StormExpression> Expression(int depth)
    {
        var token = Gen.Elements("A", "B").Select(name => (StormExpression)new StormReferenceExpr(name));
        if(depth == 0)
        {
            return token;
        }

        var inner = Expression(depth - 1);
        return Gen.Frequency(
            (4, token),
            (3, Gen.Choose(2, 3).SelectMany(count => Gen.ArrayOf(inner, count)).Select(elements => (StormExpression)new StormSequenceExpr(elements))),
            (1, Gen.Choose(2, 3).SelectMany(count => Gen.ArrayOf(inner, count)).Select(choices => (StormExpression)new StormAlternationExpr(choices))),
            (1, inner.Select(element => (StormExpression)new StormOptionalExpr(element))),
            (1, inner.Select(element => (StormExpression)new StormZeroOrMoreExpr(element))),
            (1, inner.Select(element => (StormExpression)new StormOneOrMoreExpr(element))),
            (1, Gen.Elements(true, false).SelectMany(negated => inner.Select(element => (StormExpression)new StormPredicateExpr(element, negated)))));
    }

    private static string Print(IEnumerable<StormExpression> alternatives)
    {
        return string.Join(" | ", alternatives.Select(Print));
    }

    private static string Print(StormExpression expression)
    {
        return expression switch
        {
            StormReferenceExpr reference => reference.Name,
            StormSequenceExpr sequence => "(" + string.Join(" ", sequence.Elements.Select(Print)) + ")",
            StormAlternationExpr alternation => "(" + string.Join(" | ", alternation.Alternatives.Select(Print)) + ")",
            StormOptionalExpr optional => Print(optional.Inner) + "?",
            StormZeroOrMoreExpr zeroOrMore => Print(zeroOrMore.Inner) + "*",
            StormOneOrMoreExpr oneOrMore => Print(oneOrMore.Inner) + "+",
            StormPredicateExpr predicate => (predicate.IsNegated ? "!" : "&") + Print(predicate.Inner),
            _ => expression.ToString(),
        };
    }

    private static StormGrammar Grammar(StormDeclaration[] rules)
    {
        StormDeclaration[] tokens =
        [
            .. "ABCDEFG".Select(letter => new StormTokenDecl(letter.ToString(), new StormStringLiteralExpr(char.ToLowerInvariant(letter).ToString()), 0, 1, 1)),
            new StormTokenDecl("SEMI", new StormStringLiteralExpr(";"), 0, 1, 1),
        ];
        var file = new StormFile(null, [], [.. tokens, .. rules]);

        return StormGrammar.Build(new ImportResolutionResult(
            "root",
            new Dictionary<string, StormFile> { ["root"] = file },
            [new KeyValuePair<string, StormFile>("root", file)],
            [],
            true));
    }

    private static StormRuleDecl Rule(string name, int line, StormExpression body)
    {
        return new StormRuleDecl(name, body, 0, line, 1);
    }

    private static StormExpression Tokens(string names)
    {
        var elements = names.Split(' ').Select(Ref).ToArray();
        return elements.Length == 1 ? elements[0] : new StormSequenceExpr(elements);
    }

    private static StormExpression Ref(string name)
    {
        return new StormReferenceExpr(name);
    }

    private static StormExpression Lit(string text)
    {
        return new StormStringLiteralExpr(text);
    }

    private static StormExpression Seq(params StormExpression[] elements)
    {
        return new StormSequenceExpr(elements);
    }

    private static StormExpression Or(params StormExpression[] alternatives)
    {
        return new StormAlternationExpr(alternatives);
    }

    private static StormExpression Opt(StormExpression inner)
    {
        return new StormOptionalExpr(inner);
    }

    private static StormExpression Star(StormExpression inner)
    {
        return new StormZeroOrMoreExpr(inner);
    }

    private static StormExpression Plus(StormExpression inner)
    {
        return new StormOneOrMoreExpr(inner);
    }

    private static StormExpression And(StormExpression inner)
    {
        return new StormPredicateExpr(inner, false);
    }

    private static StormExpression Not(StormExpression inner)
    {
        return new StormPredicateExpr(inner, true);
    }
}