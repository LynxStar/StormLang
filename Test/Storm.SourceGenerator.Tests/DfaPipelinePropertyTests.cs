using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Storm.SourceGenerator.Emit;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Tests;

/// <summary>
/// Property-based tests for the DFA lexer generation pipeline.
/// Tests NFA construction, subset construction, DFA minimization,
/// and the full lexer pipeline using FsCheck generators.
/// </summary>
public class DfaPipelinePropertyTests
{
    // ══════════════════════════════════════════════════════════════════
    //  10.1 — ExpressionIR Generator
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Generates depth-limited random ExpressionIR trees.
    /// Leaf nodes: StringLiteralIR, CharClassIR
    /// Composite nodes: SequenceIR, AlternationIR, OptionalIR, ZeroOrMoreIR, OneOrMoreIR
    /// </summary>
    private static Gen<ExpressionIR> GenExpressionIR(int maxDepth = 3)
    {
        if(maxDepth <= 0)
        {
            return GenLeafExpression();
        }

        return Gen.Frequency(
            (3, GenLeafExpression()),
            (1, GenSequenceIR(maxDepth - 1)),
            (1, GenAlternationIR(maxDepth - 1)),
            (1, GenOptionalIR(maxDepth - 1)),
            (1, GenZeroOrMoreIR(maxDepth - 1)),
            (1, GenOneOrMoreIR(maxDepth - 1))
        );
    }

    private static Gen<ExpressionIR> GenLeafExpression()
    {
        return Gen.Frequency(
            (3, GenStringLiteralIR()),
            (2, GenCharClassIR())
        );
    }

    /// <summary>
    /// Generates short printable ASCII strings (1-4 chars) for StringLiteralIR.
    /// </summary>
    private static Gen<ExpressionIR> GenStringLiteralIR()
    {
        // Printable ASCII chars (space through tilde), excluding problematic chars
        var printableChars = Enumerable.Range(0x21, 0x7E - 0x21 + 1)
            .Select(i => (char)i)
            .Where(c => c is not '\\' and not '"')
            .ToArray();

        return Gen.Choose(1, 4).SelectMany(len =>
            Gen.ArrayOf(Gen.Elements(printableChars), len)
                .Select(chars => (ExpressionIR)new StringLiteralIR(new string(chars))));
    }

    /// <summary>
    /// Generates valid CharClassIR expressions like [a-z], [0-9], [^abc], [A-Za-z0-9_].
    /// </summary>
    private static Gen<ExpressionIR> GenCharClassIR()
    {
        return Gen.Elements(
            "[a-z]",
            "[A-Z]",
            "[0-9]",
            "[a-zA-Z]",
            "[a-zA-Z0-9]",
            "[a-zA-Z0-9_]",
            "[^0-9]",
            "[abc]",
            "[xyz]",
            "[ \\t\\n]",
            "[^\\n]"
        ).Select(text => (ExpressionIR)new CharClassIR(text));
    }

    private static Gen<ExpressionIR> GenSequenceIR(int maxDepth)
    {
        return Gen.Choose(2, 3).SelectMany(count =>
            Gen.ArrayOf(GenExpressionIR(maxDepth), count)
                .Select(elems => (ExpressionIR)new SequenceIR(elems.ToList())));
    }

    private static Gen<ExpressionIR> GenAlternationIR(int maxDepth)
    {
        return Gen.Choose(2, 3).SelectMany(count =>
            Gen.ArrayOf(GenExpressionIR(maxDepth), count)
                .Select(alts => (ExpressionIR)new AlternationIR(alts.ToList())));
    }

    private static Gen<ExpressionIR> GenOptionalIR(int maxDepth)
    {
        return GenExpressionIR(maxDepth)
            .Select(inner => (ExpressionIR)new OptionalIR(inner));
    }

    private static Gen<ExpressionIR> GenZeroOrMoreIR(int maxDepth)
    {
        return GenExpressionIR(maxDepth)
            .Select(inner => (ExpressionIR)new ZeroOrMoreIR(inner));
    }

    private static Gen<ExpressionIR> GenOneOrMoreIR(int maxDepth)
    {
        return GenExpressionIR(maxDepth)
            .Select(inner => (ExpressionIR)new OneOrMoreIR(inner));
    }

    /// <summary>
    /// Generates random test strings from printable ASCII (0-10 chars).
    /// </summary>
    private static Gen<string> GenTestString()
    {
        var chars = Enumerable.Range(0x20, 0x7E - 0x20 + 1)
            .Select(i => (char)i)
            .ToArray();

        return Gen.Choose(0, 10).SelectMany(len =>
            Gen.ArrayOf(Gen.Elements(chars), len)
                .Select(arr => new string(arr)));
    }

    /// <summary>
    /// Generates a string that is likely to be accepted by the given expression.
    /// This helps ensure we test both accepting and rejecting cases.
    /// </summary>
    private static Gen<string> GenMatchingString(ExpressionIR expr)
    {
        switch(expr)
        {
            case StringLiteralIR lit:
                return Gen.Constant(lit.Value);
            case CharClassIR cc:
                var ranges = NfaBuilder.ParseCharClassRanges(cc.Text);
                if(ranges.Length == 0)
                {
                    return Gen.Constant("a");
                }

                return Gen.Elements(ranges).Select(r =>
                {
                    var c = (char)((r.Lo + r.Hi) / 2);
                    return c.ToString();
                });
            case SequenceIR seq:
                if(seq.Elements.Count == 0)
                {
                    return Gen.Constant("");
                }
                // Generate matching string for first element only (keep it simple)
                return GenMatchingString(seq.Elements[0]).SelectMany(first =>
                {
                    if(seq.Elements.Count == 1)
                    {
                        return Gen.Constant(first);
                    }

                    return GenMatchingString(seq.Elements[1]).Select(second => first + second);
                });
            case AlternationIR alt:
                return Gen.Elements(alt.Alternatives.ToArray())
                    .SelectMany(GenMatchingString);
            case OptionalIR opt:
                return Gen.Frequency(
                    (1, Gen.Constant("")),
                    (2, GenMatchingString(opt.Inner)));
            case ZeroOrMoreIR zm:
                return Gen.Frequency(
                    (1, Gen.Constant("")),
                    (2, GenMatchingString(zm.Inner)));
            case OneOrMoreIR om:
                return GenMatchingString(om.Inner);
            default:
                return Gen.Constant("a");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  NFA Simulation Oracle
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Simulates NFA execution on a string. Returns true if the NFA accepts
    /// the entire string (from start to end).
    /// Uses epsilon closure + character transitions.
    /// </summary>
    private static bool NfaAccepts(Nfa nfa, string input)
    {
        var statesById = new Dictionary<int, NfaState>();
        foreach(var state in nfa.States)
        {
            statesById[state.Id] = state;
        }

        var currentStates = SubsetConstructor.EpsilonClosure(
            new HashSet<int> { nfa.StartState.Id }, statesById);

        foreach(var ch in input)
        {
            var nextStates = new HashSet<int>();
            foreach(var stateId in currentStates)
            {
                if(!statesById.TryGetValue(stateId, out var state))
                {
                    continue;
                }

                foreach(var transition in state.Transitions)
                {
                    if(transition.Ranges == null)
                    {
                        continue; // skip epsilon
                    }

                    foreach(var range in transition.Ranges)
                    {
                        if(range.Contains(ch))
                        {
                            _ = nextStates.Add(transition.Target.Id);
                            break;
                        }
                    }
                }
            }

            currentStates = SubsetConstructor.EpsilonClosure(nextStates, statesById);
            if(currentStates.Count == 0)
            {
                return false;
            }
        }

        // Check if any current state is accepting
        foreach(var stateId in currentStates)
        {
            if(statesById.TryGetValue(stateId, out var state) && state.AcceptingTokenIndex >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Simulates NFA execution and returns the accepting token index with lowest
    /// declaration index, or -1 if not accepted.
    /// </summary>
    private static int NfaAcceptingToken(Nfa nfa, string input)
    {
        var statesById = new Dictionary<int, NfaState>();
        foreach(var state in nfa.States)
        {
            statesById[state.Id] = state;
        }

        var currentStates = SubsetConstructor.EpsilonClosure(
            new HashSet<int> { nfa.StartState.Id }, statesById);

        foreach(var ch in input)
        {
            var nextStates = new HashSet<int>();
            foreach(var stateId in currentStates)
            {
                if(!statesById.TryGetValue(stateId, out var state))
                {
                    continue;
                }

                foreach(var transition in state.Transitions)
                {
                    if(transition.Ranges == null)
                    {
                        continue;
                    }

                    foreach(var range in transition.Ranges)
                    {
                        if(range.Contains(ch))
                        {
                            _ = nextStates.Add(transition.Target.Id);
                            break;
                        }
                    }
                }
            }

            currentStates = SubsetConstructor.EpsilonClosure(nextStates, statesById);
            if(currentStates.Count == 0)
            {
                return -1;
            }
        }

        var bestToken = -1;
        foreach(var stateId in currentStates)
        {
            if(statesById.TryGetValue(stateId, out var state) && state.AcceptingTokenIndex >= 0)
            {
                if(bestToken < 0 || state.AcceptingTokenIndex < bestToken)
                {
                    bestToken = state.AcceptingTokenIndex;
                }
            }
        }

        return bestToken;
    }

    /// <summary>
    /// Simulates DFA execution on a string. Returns true if the DFA accepts
    /// the entire string.
    /// </summary>
    private static bool DfaAccepts(Dfa dfa, string input)
    {
        return DfaAcceptingToken(dfa, input) >= 0;
    }

    /// <summary>
    /// Simulates DFA execution and returns the accepting token index, or -1.
    /// </summary>
    private static int DfaAcceptingToken(Dfa dfa, string input)
    {
        var stateById = new Dictionary<int, DfaState>();
        foreach(var state in dfa.States)
        {
            stateById[state.Id] = state;
        }

        if(!stateById.TryGetValue(dfa.StartStateId, out var current))
        {
            return -1;
        }

        foreach(var ch in input)
        {
            DfaState? next = null;
            foreach(var transition in current.Transitions)
            {
                if(transition.Range.Contains(ch))
                {
                    _ = stateById.TryGetValue(transition.TargetStateId, out next);
                    break;
                }
            }

            if(next == null)
            {
                return -1;
            }

            current = next;
        }

        return current.AcceptingTokenIndex;
    }

    // ══════════════════════════════════════════════════════════════════
    //  10.2 — P1: NFA Round-Trip
    //  For any ExpressionIR and any string, NFA acceptance matches
    //  the DFA acceptance (both built from the same expression).
    //  **Validates: Requirements 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.7, 1.8, 1.9, 1.10, 11.1, 11.3**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 100)]
    public Property P1_NfaRoundTrip_NfaAcceptanceMatchesDfa()
    {
        var gen = GenExpressionIR(3).SelectMany(expr =>
            Gen.Frequency(
                (1, GenTestString()),
                (2, GenMatchingString(expr))
            ).Select(input => (expr, input)));

        return Prop.ForAll(gen.ToArbitrary(), pair =>
        {
            var (expr, input) = pair;

            var builder = new NfaBuilder();
            var fragment = builder.BuildFragment(expr);
            fragment.Accept.AcceptingTokenIndex = 0;
            var nfa = new Nfa(fragment.Start);

            var nfaResult = NfaAccepts(nfa, input);

            // Build a single-token combined NFA, convert to DFA, and check
            var token = new TokenIR("T", expr, false, 0);
            var builder2 = new NfaBuilder();
            var combinedNfa = builder2.BuildCombined(new List<TokenIR> { token });
            var dfa = SubsetConstructor.Convert(combinedNfa);
            var dfaResult = DfaAccepts(dfa, input);

            Assert.Equal(nfaResult, dfaResult);
        });
    }

    // ══════════════════════════════════════════════════════════════════
    //  10.3 — P2: Combined NFA Union
    //  For any set of token expressions and any string, combined NFA
    //  accepts iff any individual NFA accepts.
    //  **Validates: Requirements 2.1**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 100)]
    public Property P2_CombinedNfaUnion_CombinedAcceptsIffAnyIndividualAccepts()
    {
        var genTokenExprs = Gen.Choose(2, 4).SelectMany(count =>
            Gen.ArrayOf(GenExpressionIR(2), count));

        var gen = genTokenExprs.SelectMany(exprs =>
        {
            var matchGens = exprs.Select(e => GenMatchingString(e)).ToArray();
            var combinedMatchGen = Gen.Elements(matchGens).SelectMany(g => g);

            return Gen.Frequency(
                (1, GenTestString()),
                (3, combinedMatchGen)
            ).Select(input => (exprs, input));
        });

        return Prop.ForAll(gen.ToArbitrary(), pair =>
        {
            var (exprs, input) = pair;

            // Build individual NFAs and check acceptance
            var anyIndividualAccepts = false;
            for(var i = 0; i < exprs.Length; i++)
            {
                var builder = new NfaBuilder();
                var fragment = builder.BuildFragment(exprs[i]);
                fragment.Accept.AcceptingTokenIndex = i;
                var individualNfa = new Nfa(fragment.Start);
                if(NfaAccepts(individualNfa, input))
                {
                    anyIndividualAccepts = true;
                    break;
                }
            }

            // Build combined NFA
            var tokens = exprs.Select((e, i) =>
                new TokenIR($"T{i}", e, false, i)).ToList();
            var combinedBuilder = new NfaBuilder();
            var combinedNfa = combinedBuilder.BuildCombined(tokens);
            var combinedAccepts = NfaAcceptingToken(combinedNfa, input) >= 0;

            Assert.Equal(anyIndividualAccepts, combinedAccepts);
        });
    }

    // ══════════════════════════════════════════════════════════════════
    //  10.4 — P3: Subset Construction
    //  For any combined NFA and any string, DFA accepts iff NFA accepts,
    //  with correct priority.
    //  **Validates: Requirements 3.1, 3.2, 3.3**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 100)]
    public Property P3_SubsetConstruction_DfaAcceptsIffNfaAcceptsWithCorrectPriority()
    {
        var genTokenExprs = Gen.Choose(2, 4).SelectMany(count =>
            Gen.ArrayOf(GenExpressionIR(2), count));

        var gen = genTokenExprs.SelectMany(exprs =>
        {
            var matchGens = exprs.Select(e => GenMatchingString(e)).ToArray();
            var combinedMatchGen = Gen.Elements(matchGens).SelectMany(g => g);

            return Gen.Frequency(
                (1, GenTestString()),
                (3, combinedMatchGen)
            ).Select(input => (exprs, input));
        });

        return Prop.ForAll(gen.ToArbitrary(), pair =>
        {
            var (exprs, input) = pair;

            var tokens = exprs.Select((e, i) =>
                new TokenIR($"T{i}", e, false, i)).ToList();

            var builder = new NfaBuilder();
            var combinedNfa = builder.BuildCombined(tokens);

            // NFA acceptance with priority
            var nfaToken = NfaAcceptingToken(combinedNfa, input);

            // DFA acceptance
            var dfa = SubsetConstructor.Convert(combinedNfa);
            var dfaToken = DfaAcceptingToken(dfa, input);

            // Both should agree on acceptance
            Assert.Equal(nfaToken >= 0, dfaToken >= 0);

            // When both accept, DFA should return the lowest declaration index
            if(nfaToken >= 0 && dfaToken >= 0)
            {
                Assert.Equal(nfaToken, dfaToken);
            }
        });
    }

    // ══════════════════════════════════════════════════════════════════
    //  10.5 — P4: Minimization Correctness
    //  For any DFA and any string, minimized DFA accepts iff original
    //  DFA accepts with same token.
    //  **Validates: Requirements 4.1, 4.2, 4.3**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 100)]
    public Property P4_MinimizationCorrectness_MinimizedDfaMatchesOriginal()
    {
        var genTokenExprs = Gen.Choose(1, 4).SelectMany(count =>
            Gen.ArrayOf(GenExpressionIR(2), count));

        var gen = genTokenExprs.SelectMany(exprs =>
        {
            var matchGens = exprs.Select(e => GenMatchingString(e)).ToArray();
            var combinedMatchGen = Gen.Elements(matchGens).SelectMany(g => g);

            return Gen.Frequency(
                (1, GenTestString()),
                (3, combinedMatchGen)
            ).Select(input => (exprs, input));
        });

        return Prop.ForAll(gen.ToArbitrary(), pair =>
        {
            var (exprs, input) = pair;

            var tokens = exprs.Select((e, i) =>
                new TokenIR($"T{i}", e, false, i)).ToList();

            var builder = new NfaBuilder();
            var combinedNfa = builder.BuildCombined(tokens);
            var dfa = SubsetConstructor.Convert(combinedNfa);
            var minimized = DfaMinimizer.Minimize(dfa);

            var originalToken = DfaAcceptingToken(dfa, input);
            var minimizedToken = DfaAcceptingToken(minimized, input);

            // Both should agree on acceptance
            Assert.Equal(originalToken >= 0, minimizedToken >= 0);

            // When both accept, same token
            if(originalToken >= 0 && minimizedToken >= 0)
            {
                Assert.Equal(originalToken, minimizedToken);
            }
        });
    }

    // ══════════════════════════════════════════════════════════════════
    //  10.6 — P5: Minimization Idempotence
    //  For any DFA, minimize(minimize(dfa)).StateCount == minimize(dfa).StateCount
    //  **Validates: Requirements 4.4**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 100)]
    public Property P5_MinimizationIdempotence_DoubleMinimizeSameStateCount()
    {
        var genTokenExprs = Gen.Choose(1, 4).SelectMany(count =>
            Gen.ArrayOf(GenExpressionIR(2), count));

        return Prop.ForAll(genTokenExprs.ToArbitrary(), exprs =>
        {
            var tokens = exprs.Select((e, i) =>
                new TokenIR($"T{i}", e, false, i)).ToList();

            var builder = new NfaBuilder();
            var combinedNfa = builder.BuildCombined(tokens);
            var dfa = SubsetConstructor.Convert(combinedNfa);

            var minimized1 = DfaMinimizer.Minimize(dfa);
            var minimized2 = DfaMinimizer.Minimize(minimized1);

            Assert.Equal(minimized1.States.Count, minimized2.States.Count);
        });
    }

    // ══════════════════════════════════════════════════════════════════
    //  10.7 — P10: Full Lexer Equivalence (DFA Pipeline End-to-End)
    //  For any input string, the DFA pipeline produces consistent results.
    //  We verify that the full pipeline (NFA → DFA → minimize → tables)
    //  produces correct token streams on known patterns.
    //  **Validates: Requirements 12.1, 12.2, 12.3, 12.4, 12.5**
    // ══════════════════════════════════════════════════════════════════

    [Property(MaxTest = 100)]
    public Property P10_FullLexerEquivalence_DfaPipelineConsistentWithNfa()
    {
        // Define a small grammar: identifiers, numbers, whitespace
        var identExpr = (ExpressionIR)new SequenceIR(new List<ExpressionIR>
        {
            new CharClassIR("[a-zA-Z_]"),
            new ZeroOrMoreIR(new CharClassIR("[a-zA-Z0-9_]"))
        });
        var numberExpr = (ExpressionIR)new OneOrMoreIR(new CharClassIR("[0-9]"));
        var wsExpr = (ExpressionIR)new OneOrMoreIR(new CharClassIR("[ \\t\\n]"));
        var plusExpr = (ExpressionIR)new StringLiteralIR("+");
        var eqExpr = (ExpressionIR)new StringLiteralIR("=");

        var tokens = new List<TokenIR>
        {
            new("Ident", identExpr, false, 0),
            new("Number", numberExpr, false, 1),
            new("WS", wsExpr, true, 2),
            new("Plus", plusExpr, false, 3),
            new("Eq", eqExpr, false, 4),
        };

        // Build the full DFA pipeline
        var nfaBuilder = new NfaBuilder();
        var combinedNfa = nfaBuilder.BuildCombined(tokens);
        var dfa = SubsetConstructor.Convert(combinedNfa);
        var minimized = DfaMinimizer.Minimize(dfa);
        var (charToClass, classCount) = EquivalenceClassComputer.Compute(minimized);

        // Build transition and accepting tables
        var stateIdToIndex = new Dictionary<int, int>();
        for(var i = 0; i < minimized.States.Count; i++)
        {
            stateIdToIndex[minimized.States[i].Id] = i;
        }

        var transTable = new short[minimized.States.Count, classCount];
        var acceptTable = new short[minimized.States.Count];

        for(var s = 0; s < minimized.States.Count; s++)
        {
            for(var c = 0; c < classCount; c++)
            {
                transTable[s, c] = -1;
            }
        }

        for(var i = 0; i < minimized.States.Count; i++)
        {
            var state = minimized.States[i];
            var si = stateIdToIndex[state.Id];
            acceptTable[si] = (short)state.AcceptingTokenIndex;

            foreach(var trans in state.Transitions)
            {
                if(!stateIdToIndex.TryGetValue(trans.TargetStateId, out var ti))
                {
                    continue;
                }

                var seenClasses = new HashSet<int>();
                for(int c = trans.Range.Lo; c <= trans.Range.Hi; c++)
                {
                    var eqClass = charToClass[c];
                    if(seenClasses.Add(eqClass))
                    {
                        transTable[si, eqClass] = (short)ti;
                    }
                }
            }
        }

        var startIndex = stateIdToIndex[minimized.StartStateId];

        // Generate random inputs and verify table-driven lexing matches NFA simulation
        var inputChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_ \t\n+=!@#"
            .ToCharArray();

        var gen = Gen.Choose(0, 20).SelectMany(len =>
            Gen.ArrayOf(Gen.Elements(inputChars), len)
                .Select(arr => new string(arr)));

        return Prop.ForAll(gen.ToArbitrary(), input =>
        {
            // Table-driven lexing (simulating the emitted code)
            var tableLexTokens = new List<(int tokenKind, string lexeme)>();
            var pos = 0;
            while(pos < input.Length)
            {
                var state = startIndex;
                var lastAcceptKind = -1;
                var lastAcceptPos = pos;
                var scanPos = pos;

                if(acceptTable[state] >= 0)
                {
                    lastAcceptKind = acceptTable[state];
                    lastAcceptPos = scanPos;
                }

                while(scanPos < input.Length)
                {
                    var ch = input[scanPos];
                    var cls = charToClass[ch];
                    var next = transTable[state, cls];
                    if(next < 0)
                    {
                        break;
                    }

                    state = next;
                    scanPos++;
                    if(acceptTable[state] >= 0)
                    {
                        lastAcceptKind = acceptTable[state];
                        lastAcceptPos = scanPos;
                    }
                }

                if(lastAcceptKind >= 0 && lastAcceptPos > pos)
                {
                    tableLexTokens.Add((lastAcceptKind, input[pos..lastAcceptPos]));
                    pos = lastAcceptPos;
                }
                else
                {
                    tableLexTokens.Add((-1, input.Substring(pos, 1))); // error token
                    pos++;
                }
            }

            // NFA-based lexing (oracle): same maximal munch strategy
            var nfaLexTokens = new List<(int tokenKind, string lexeme)>();
            pos = 0;
            while(pos < input.Length)
            {
                var bestToken = -1;
                var bestLen = 0;

                // Try each length from 1 to remaining, find longest match
                for(var len = 1; len <= input.Length - pos; len++)
                {
                    var substr = input.Substring(pos, len);
                    var token = NfaAcceptingToken(combinedNfa, substr);
                    if(token >= 0)
                    {
                        bestToken = token;
                        bestLen = len;
                    }
                }

                if(bestLen > 0)
                {
                    nfaLexTokens.Add((bestToken, input.Substring(pos, bestLen)));
                    pos += bestLen;
                }
                else
                {
                    nfaLexTokens.Add((-1, input.Substring(pos, 1)));
                    pos++;
                }
            }

            // Compare token streams
            Assert.Equal(nfaLexTokens.Count, tableLexTokens.Count);
            for(var i = 0; i < nfaLexTokens.Count; i++)
            {
                Assert.Equal(nfaLexTokens[i].lexeme, tableLexTokens[i].lexeme);
                Assert.Equal(nfaLexTokens[i].tokenKind, tableLexTokens[i].tokenKind);
            }
        });
    }
}