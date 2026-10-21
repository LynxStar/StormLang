using Storm.SourceGenerator.Frontend;

namespace Storm.SourceGenerator.Tests;

[Trait("Tier", "1")]
public sealed class StormPrettyPrinterTests
{
    [Fact]
    public void EveryPrintedLineEndsInLineFeedAlone_WhateverTheHostsNewLine()
    {
        var file = new StormFile(
            new StormGrammarDecl("Printed", 0, 1, 1),
            [new StormImport("lib.storm", "L", 0, 2, 1), new StormImport("tokens.storm", null, 0, 3, 1)],
            [
                new StormFragmentDecl("DIGIT", new StormCharClassExpr("[0-9]"), 0, 4, 1),
                new StormTokenDecl("OPEN", new StormStringLiteralExpr("{"), 0, 5, 1, new StormPushAction("Inner")),
                new StormModeDecl(
                    "Inner",
                    [
                        new StormTokenDecl("CLOSE", new StormStringLiteralExpr("}"), 0, 7, 5, new StormPopAction()),
                        new StormHiddenTokenDecl("WS", new StormCharClassExpr("[ ]"), 0, 8, 5),
                    ],
                    0, 6, 1),
                new StormTraitDecl("Numbered", [new StormTraitField("value", "DIGIT", StormFieldCardinality.OneOrMore)], [], 0, 10, 1),
                new StormRuleDecl(
                    "Thing",
                    new StormSequenceExpr([new StormReferenceExpr("OPEN"), new StormReferenceExpr("CLOSE")]),
                    0, 11, 1, IsMemo: true, TraitBindings: ["Numbered"]),
            ]);

        var printed = StormPrettyPrinter.Print(file);

        Assert.DoesNotContain('\r', printed);
        Assert.Equal(
            "grammar Printed;\n\nimport \"lib.storm\" as L;\nimport \"tokens.storm\";\n\n"
            + "fragment DIGIT = [0-9];\n\ntoken OPEN = \"{\" => push Inner;\n\n"
            + "mode Inner {\n    token CLOSE = \"}\" => pop;\n    hidden token WS = [ ];\n}\n\n"
            + "trait Numbered = value:DIGIT+;\n\n@memo\nrule Thing : Numbered = OPEN CLOSE;\n",
            printed);
    }
}