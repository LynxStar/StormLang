using Storm.SourceGenerator.Emit;

namespace Storm.SourceGenerator.Tests;

public class EquivalenceClassComputerTests
{
    // ── Empty / trivial DFAs ─────────────────────────────────────────

    [Fact]
    public void Compute_EmptyDfa_ReturnsSingleClass()
    {
        var dfa = new Dfa(0, new List<DfaState>());

        var (charToClass, classCount) = EquivalenceClassComputer.Compute(dfa);

        Assert.Equal(65536, charToClass.Length);
        Assert.Equal(1, classCount);
        // All characters should be in class 0
        Assert.Equal(0, charToClass[0]);
        Assert.Equal(0, charToClass[65535]);
    }

    [Fact]
    public void Compute_SingleStateNoTransitions_ReturnsSingleClass()
    {
        var state = new DfaState(0) { AcceptingTokenIndex = 0 };
        var dfa = new Dfa(0, new List<DfaState> { state });

        var (charToClass, classCount) = EquivalenceClassComputer.Compute(dfa);

        Assert.Equal(1, classCount);
        for(var c = 0; c < 65536; c++)
        {
            Assert.Equal(0, charToClass[c]);
        }
    }

    // ── Single character transition ──────────────────────────────────

    [Fact]
    public void Compute_SingleCharTransition_CreatesTwoClasses()
    {
        // DFA: state 0 --'a'--> state 1 (accepting)
        var s0 = new DfaState(0);
        var s1 = new DfaState(1) { AcceptingTokenIndex = 0 };
        s0.Transitions.Add(new DfaTransition(new CharRange('a', 'a'), 1));
        var dfa = new Dfa(0, new List<DfaState> { s0, s1 });

        var (charToClass, classCount) = EquivalenceClassComputer.Compute(dfa);

        Assert.Equal(2, classCount);
        // 'a' should be in a different class than other characters
        var classA = charToClass['a'];
        var classOther = charToClass['b'];
        Assert.NotEqual(classA, classOther);
        // All non-'a' characters should be in the same class
        Assert.Equal(charToClass['b'], charToClass['z']);
        Assert.Equal(charToClass['b'], charToClass[0]);
    }

    // ── Range transition ─────────────────────────────────────────────

    [Fact]
    public void Compute_RangeTransition_GroupsRangeCharsTogether()
    {
        // DFA: state 0 --[a-z]--> state 1 (accepting)
        var s0 = new DfaState(0);
        var s1 = new DfaState(1) { AcceptingTokenIndex = 0 };
        s0.Transitions.Add(new DfaTransition(new CharRange('a', 'z'), 1));
        var dfa = new Dfa(0, new List<DfaState> { s0, s1 });

        var (charToClass, classCount) = EquivalenceClassComputer.Compute(dfa);

        Assert.Equal(2, classCount);
        // All chars in [a-z] should be in the same class
        var classAZ = charToClass['a'];
        for(var c = 'b'; c <= 'z'; c++)
        {
            Assert.Equal(classAZ, charToClass[c]);
        }
        // Characters outside [a-z] should be in a different class
        Assert.NotEqual(classAZ, charToClass['A']);
        Assert.NotEqual(classAZ, charToClass['0']);
    }

    // ── Multiple distinct transitions ────────────────────────────────

    [Fact]
    public void Compute_TwoDistinctRanges_CreatesThreeClasses()
    {
        // DFA: state 0 --[a-z]--> state 1, --[0-9]--> state 2
        var s0 = new DfaState(0);
        var s1 = new DfaState(1) { AcceptingTokenIndex = 0 };
        var s2 = new DfaState(2) { AcceptingTokenIndex = 1 };
        s0.Transitions.Add(new DfaTransition(new CharRange('a', 'z'), 1));
        s0.Transitions.Add(new DfaTransition(new CharRange('0', '9'), 2));
        var dfa = new Dfa(0, new List<DfaState> { s0, s1, s2 });

        var (charToClass, classCount) = EquivalenceClassComputer.Compute(dfa);

        // Should have 3 classes: [a-z], [0-9], and everything else
        Assert.Equal(3, classCount);
        var classAZ = charToClass['a'];
        var classDigit = charToClass['0'];
        var classOther = charToClass['!'];
        Assert.NotEqual(classAZ, classDigit);
        Assert.NotEqual(classAZ, classOther);
        Assert.NotEqual(classDigit, classOther);
    }

    // ── Same target from different states ────────────────────────────

    [Fact]
    public void Compute_SameRangeDifferentTargets_SplitsClasses()
    {
        // DFA: state 0 --[a-z]--> state 1
        //      state 1 --[a-m]--> state 2, --[n-z]--> state 3
        // Characters a-m and n-z have different signatures because
        // state 1 transitions them to different targets.
        var s0 = new DfaState(0);
        var s1 = new DfaState(1);
        var s2 = new DfaState(2) { AcceptingTokenIndex = 0 };
        var s3 = new DfaState(3) { AcceptingTokenIndex = 1 };
        s0.Transitions.Add(new DfaTransition(new CharRange('a', 'z'), 1));
        s1.Transitions.Add(new DfaTransition(new CharRange('a', 'm'), 2));
        s1.Transitions.Add(new DfaTransition(new CharRange('n', 'z'), 3));
        var dfa = new Dfa(0, new List<DfaState> { s0, s1, s2, s3 });
        var (charToClass, _) = EquivalenceClassComputer.Compute(dfa);

        // a-m and n-z should be in different classes because they have
        // different transition signatures across all states
        var classAM = charToClass['a'];
        var classNZ = charToClass['n'];
        Assert.NotEqual(classAM, classNZ);
        // Characters within each sub-range should share a class
        Assert.Equal(classAM, charToClass['m']);
        Assert.Equal(classNZ, charToClass['z']);
    }

    // ── charToClass array size ───────────────────────────────────────

    [Fact]
    public void Compute_AlwaysReturns65536Entries()
    {
        var s0 = new DfaState(0);
        s0.Transitions.Add(new DfaTransition(new CharRange('x', 'x'), 0));
        var dfa = new Dfa(0, new List<DfaState> { s0 });

        var (charToClass, _) = EquivalenceClassComputer.Compute(dfa);

        Assert.Equal(65536, charToClass.Length);
    }

    // ── Class indices are contiguous from 0 ──────────────────────────

    [Fact]
    public void Compute_ClassIndicesAreContiguousFromZero()
    {
        var s0 = new DfaState(0);
        var s1 = new DfaState(1) { AcceptingTokenIndex = 0 };
        var s2 = new DfaState(2) { AcceptingTokenIndex = 1 };
        s0.Transitions.Add(new DfaTransition(new CharRange('a', 'z'), 1));
        s0.Transitions.Add(new DfaTransition(new CharRange('0', '9'), 2));
        var dfa = new Dfa(0, new List<DfaState> { s0, s1, s2 });

        var (charToClass, classCount) = EquivalenceClassComputer.Compute(dfa);

        var usedClasses = new HashSet<int>();
        for(var c = 0; c < 65536; c++)
        {
            _ = usedClasses.Add(charToClass[c]);
        }

        Assert.Equal(classCount, usedClasses.Count);
        for(var i = 0; i < classCount; i++)
        {
            Assert.Contains(i, usedClasses);
        }
    }

    // ── High Unicode range ───────────────────────────────────────────

    [Fact]
    public void Compute_HighUnicodeRange_HandledCorrectly()
    {
        // Transition on a high Unicode range
        var s0 = new DfaState(0);
        var s1 = new DfaState(1) { AcceptingTokenIndex = 0 };
        s0.Transitions.Add(new DfaTransition(
            new CharRange((char)0xFF00, (char)0xFFFF), 1));
        var dfa = new Dfa(0, new List<DfaState> { s0, s1 });

        var (charToClass, classCount) = EquivalenceClassComputer.Compute(dfa);

        Assert.Equal(2, classCount);
        var classHigh = charToClass[0xFF00];
        Assert.Equal(classHigh, charToClass[0xFFFF]);
        Assert.NotEqual(classHigh, charToClass[0]);
        Assert.NotEqual(classHigh, charToClass['a']);
    }
}