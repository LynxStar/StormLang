using System;
using System.Collections.Generic;
using System.Linq;
using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Frontend;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Validate;

/// <summary>
/// Builds the rule-call graph from the effective grammar (only rules, not tokens/fragments)
/// and detects direct and indirect left-recursion cycles. A rule is left-recursive if it
/// can reach itself through the leftmost position of its alternatives.
/// Emits STORM_LEFT_RECURSION diagnostics with the cycle path.
/// </summary>
public static class LeftRecursionPass
{
    public static List<StormDiagnostic> Run(StormGrammar grammar)
    {
        return Run(grammar.Declarations, grammar.NamespacedDeclarations, grammar.LookupByName);
    }

    internal static List<StormDiagnostic> Run(
        IReadOnlyList<StormDeclaration> declarations,
        IReadOnlyDictionary<string, StormDeclaration> namespacedDeclarations,
        Func<string, StormDeclaration?> lookup)
    {
        var graph = new LeftCallGraph(declarations, lookup);
        var names = graph.Rules.Select(rule => NameOf(rule, namespacedDeclarations, lookup)).ToList();
        var search = new CycleSearch(graph, names);
        for(var rule = 0; rule < graph.Rules.Count; rule++)
        {
            search.From(rule);
        }

        return search.Diagnostics;
    }

    private static string NameOf(
        StormRuleDecl rule,
        IReadOnlyDictionary<string, StormDeclaration> namespacedDeclarations,
        Func<string, StormDeclaration?> lookup)
    {
        if(ReferenceEquals(lookup(rule.Name), rule))
        {
            return rule.Name;
        }

        foreach(var entry in namespacedDeclarations)
        {
            if(ReferenceEquals(entry.Value, rule))
            {
                return entry.Key;
            }
        }

        return rule.Name;
    }

    private sealed class CycleSearch
    {
        private readonly LeftCallGraph graph;
        private readonly IReadOnlyList<string> names;
        private readonly bool[] visited;
        private readonly bool[] onStack;
        private readonly bool[] reported;
        private readonly List<int> path = [];

        public CycleSearch(LeftCallGraph graph, IReadOnlyList<string> names)
        {
            this.graph = graph;
            this.names = names;
            visited = new bool[graph.Rules.Count];
            onStack = new bool[graph.Rules.Count];
            reported = new bool[graph.Rules.Count];
        }

        public List<StormDiagnostic> Diagnostics { get; } = [];

        public void From(int rule)
        {
            if(visited[rule])
            {
                return;
            }

            path.Add(rule);
            onStack[rule] = true;
            foreach(var callee in graph.Calls(rule))
            {
                if(onStack[callee])
                {
                    Report(callee);
                }
                else
                {
                    From(callee);
                }
            }

            path.RemoveAt(path.Count - 1);
            onStack[rule] = false;
            visited[rule] = true;
        }

        private void Report(int entry)
        {
            if(reported[entry])
            {
                return;
            }

            reported[entry] = true;
            var cycle = path.Skip(path.IndexOf(entry)).Append(entry).Select(rule => names[rule]);
            var declaration = graph.Rules[entry];
            Diagnostics.Add(new StormDiagnostic(
                StormDiagnosticIds.STORM_LEFT_RECURSION,
                StormDiagnosticSeverity.Error,
                $"Left-recursion detected: {string.Join(" -> ", cycle)}",
                null,
                declaration.Line,
                declaration.Column));
        }
    }
}