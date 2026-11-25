// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Translation.Core;

namespace FiveStar.Shaders.Translation.Structure;

/// <summary>
/// Makes an irreducible graph reducible by giving each cycle entered at several blocks one header that dispatches to
/// them: every edge into one of the entries sets a selector (<c>irr_entry&lt;n&gt; = &lt;i&gt;u</c>) in a block of its own and
/// goes to the header, a chain of tests on the selector. Only that cycle becomes a loop with a selector; the rest of the
/// program stays structured, where it used to fall back whole to the dispatch form (about three times the driver's
/// compile time and several times its memory on the same program). The search is LLVM's FixIrreducible: the strongly
/// connected components of a region (the program, then each component without the edges back to its header), a
/// component entered at more than one block is guarded, and each component is searched again inside.
/// </summary>
internal static class EntryGuards
{
    /// <summary>The selector variables are named with this prefix and their number.</summary>
    public const string SelectorPrefix = "irr_entry";

    /// <summary>
    /// The graph with every multi-entry cycle guarded, its entry (a selector block when the program entry itself is
    /// one of a cycle's entries) and the number of selectors; the graph itself when it is reducible.
    /// </summary>
    public static (FlowGraph Graph, int Entry, int Selectors) Apply(FlowGraph graph, int entry)
    {
        var editor = new Editor(graph, entry);
        editor.Fix(editor.Reachable(), header: null);

        if (editor.Selectors == 0)
        {
            return (graph, entry, 0);
        }

        return (editor.Build(), editor.Entry, editor.Selectors);
    }

    /// <summary>The declaration of the selectors, a statement of the function body.</summary>
    public static string Declaration(int selectors)
    {
        return "uint " + string.Join(", ", Enumerable.Range(0, selectors).Select(index => $"{SelectorPrefix}{index} = 0u")) + ";";
    }

    private sealed class Editor
    {
        // The program end while editing: the end node moves as blocks are added.
        private const int EndMarker = -1;

        private readonly List<CodeBlock> _blocks;
        private readonly List<List<int>> _successors;
        private readonly List<List<int>> _predecessors;
        private readonly HashSet<int> _live;

        public Editor(FlowGraph graph, int entry)
        {
            _blocks = [.. graph.Blocks];
            _successors = [.. graph.Successors.Take(graph.End).Select(targets => targets.Select(target => Renamed(target, graph.End, EndMarker)).ToList())];
            _predecessors = [.. _successors.Select(_ => new List<int>())];

            for (int node = 0; node < _successors.Count; node++)
            {
                foreach (var target in _successors[node].Where(target => target != EndMarker))
                {
                    _predecessors[target].Add(node);
                }
            }

            Entry = entry;
            _live = Reachable();
        }

        public int Entry { get; private set; }

        public int Selectors { get; private set; }

        /// <summary>The blocks reached from the entry; an unreachable block neither enters a cycle nor gets guarded.</summary>
        public HashSet<int> Reachable()
        {
            var seen = new HashSet<int> { Entry };
            var pending = new Stack<int>();
            pending.Push(Entry);

            while (pending.Count > 0)
            {
                foreach (var target in _successors[pending.Pop()].Where(target => target != EndMarker && seen.Add(target)))
                {
                    pending.Push(target);
                }
            }

            return seen;
        }

        /// <summary>Guards the multi-entry components of <paramref name="region"/>, the edges back to <paramref name="header"/> left out.</summary>
        public void Fix(HashSet<int> region, int? header)
        {
            foreach (var component in Components(region, header))
            {
                var entries = Entries(component);

                if (entries.Count > 1)
                {
                    int guard = Guard(component, entries);
                    Fix(component, guard);

                    continue;
                }

                Fix(component, entries[0]);
            }
        }

        public FlowGraph Build()
        {
            int end = _blocks.Count;
            var successors = new int[end + 1][];

            for (int node = 0; node < end; node++)
            {
                successors[node] = [.. _successors[node].Select(target => Renamed(target, EndMarker, end))];
            }

            successors[end] = [];

            return FlowGraph.WithEdges(_blocks, successors);
        }

        /// <summary><paramref name="to"/> for <paramref name="from"/>, any other node as it is.</summary>
        private static int Renamed(int node, int from, int to)
        {
            if (node == from)
            {
                return to;
            }

            return node;
        }

        /// <summary>The blocks of the component reached from a live block outside it, and the program entry; in node order.</summary>
        private List<int> Entries(HashSet<int> component)
        {
            return [.. component.Where(node => node == Entry || _predecessors[node].Any(source => _live.Contains(source) && !component.Contains(source))).Order()];
        }

        /// <summary>
        /// Adds the header and the selector blocks; returns the header. Every edge into an entry, from outside the
        /// component or inside it, now sets the selector and goes to the header; the header's tests and the selector
        /// blocks on edges inside the component join the component.
        /// </summary>
        private int Guard(HashSet<int> component, List<int> entries)
        {
            string selector = SelectorPrefix + Selectors;
            Selectors++;

            var tests = new int[entries.Count - 1];

            for (int index = 0; index < tests.Length; index++)
            {
                tests[index] = Add(new CodeBlock(-1, ["// entry guard"], new BlockExit(BlockExitKind.Conditional, null, null, $"({selector} == {index}u)")));
                _live.Add(tests[index]);
                component.Add(tests[index]);
            }

            // Test i takes entry i, else the next test; the last test's else is the last entry.
            for (int index = 0; index < tests.Length; index++)
            {
                Link(tests[index], entries[index]);
            }

            for (int index = 0; index + 1 < tests.Length; index++)
            {
                Link(tests[index], tests[index + 1]);
            }

            Link(tests[^1], entries[^1]);

            int header = tests[0];

            for (int index = 0; index < entries.Count; index++)
            {
                foreach (var source in _predecessors[entries[index]].Where(source => Array.IndexOf(tests, source) < 0).Distinct().ToList())
                {
                    int set = SetSelector(selector, index, header, _live.Contains(source));
                    Redirect(source, entries[index], set);

                    if (component.Contains(source))
                    {
                        component.Add(set);
                    }
                }
            }

            if (entries.Contains(Entry))
            {
                Entry = SetSelector(selector, entries.IndexOf(Entry), header, live: true);
            }

            return header;
        }

        /// <summary>A block that sets the selector to <paramref name="index"/> and goes to the header.</summary>
        private int SetSelector(string selector, int index, int header, bool live)
        {
            int node = Add(new CodeBlock(-1, [$"{selector} = {index}u;", "// to entry guard"], new BlockExit(BlockExitKind.Jump, null, null, null)));
            Link(node, header);

            if (live)
            {
                _live.Add(node);
            }

            return node;
        }

        /// <summary>Every edge from <paramref name="source"/> to <paramref name="from"/> goes to <paramref name="to"/> instead, in the same slot (a branch keeps its taken target first).</summary>
        private void Redirect(int source, int from, int to)
        {
            var targets = _successors[source];

            for (int slot = 0; slot < targets.Count; slot++)
            {
                if (targets[slot] == from)
                {
                    targets[slot] = to;
                    _predecessors[from].Remove(source);
                    _predecessors[to].Add(source);
                }
            }
        }

        private void Link(int source, int target)
        {
            _successors[source].Add(target);
            _predecessors[target].Add(source);
        }

        private int Add(CodeBlock block)
        {
            _blocks.Add(block);
            _successors.Add([]);
            _predecessors.Add([]);

            return _blocks.Count - 1;
        }

        /// <summary>The strongly connected components of the region that hold a cycle.</summary>
        private List<HashSet<int>> Components(HashSet<int> region, int? header)
        {
            return new CycleComponents(_successors, region, header, EndMarker).Find();
        }
    }
}
