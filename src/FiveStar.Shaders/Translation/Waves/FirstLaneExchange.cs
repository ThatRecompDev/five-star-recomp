// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;
using FiveStar.Shaders.Translation.Core;
using FiveStar.Shaders.Translation.Structure;

namespace FiveStar.Shaders.Translation.Waves;

/// <summary>
/// The v_readfirstlanes a two-phase mesh program exchanges through groupshared memory so that it gives console results
/// on narrower hardware waves. A hardware wave holding only part of the console wave reads its own first lane, so a
/// readfirstlane of a per-lane value (TransformD's vertex and primitive counts from each lane's meshlet record, which
/// feed GS_ALLOC_REQ and every lane's export test) holds the window analysis back. Under a full EXEC the console's
/// value is lane 0's: lane 0 deposits it and the block yields to the group's barrier step (one round, as an s_barrier),
/// after which every thread of the console wave reads it. Only before GS_ALLOC_REQ, where no hardware wave can skip a
/// region holding a yield (<see cref="SkipRegion"/>), so all reach each yield in the same round. The exchange is kept
/// only when the window analysis then passes: it costs a group barrier per site.
/// </summary>
internal static partial class FirstLaneExchange
{
    /// <summary>Whether the program can take exchanges: a two-phase mesh program in the emulated (window) form.</summary>
    public static bool Applies(TranslationState state)
    {
        return state.Stage == ShaderStage.Mesh && state.MeshYield && state.EmulateWaves && !state.FastWaves && !state.Supersteps;
    }

    /// <summary>
    /// The instructions to exchange (address to site), and those of the mesh thread id that need none (address to the
    /// console's value: under a full EXEC its first lane is the console wave's first thread).
    /// </summary>
    public static (Dictionary<long, int> Sites, Dictionary<long, string> Direct) Sites(TranslationState state)
    {
        var sites = new Dictionary<long, int>();
        var direct = new Dictionary<long, string>();
        var entries = new WindowAnalysis(state.Blocks, state.Prologue, state.MeshThreads).EntryFacts();

        if (entries is null)
        {
            return (sites, direct);
        }

        var starts = state.Blocks.Select(block => (long)block.Address).Order().ToList();

        foreach (var block in state.Blocks)
        {
            if (!entries.TryGetValue(block.Address, out var entry))
            {
                continue;
            }

            var ordinals = Qualifying(block, entry, state.MeshThreads);

            if (ordinals.Count == 0)
            {
                continue;
            }

            var end = starts.FirstOrDefault(start => start > block.Address, long.MaxValue);
            var reads = state.Instructions.Where(instruction => instruction.Address >= block.Address && instruction.Address < end
                && instruction.Mnemonic == "v_readfirstlane_b32").ToList();
            var lines = block.Lines.Count(line => FirstLanePattern().IsMatch(line));

            if (reads.Count != lines)
            {
                continue;
            }

            var lineIndexes = Enumerable.Range(0, block.Lines.Count).Where(index => FirstLanePattern().IsMatch(block.Lines[index])).ToList();

            foreach (var ordinal in ordinals)
            {
                var id = ThreadId(state, block, lineIndexes[ordinal]);

                if (id is not null)
                {
                    direct[reads[ordinal].Address] = id;
                }
                else
                {
                    sites[reads[ordinal].Address] = sites.Count;
                }
            }
        }

        return (sites, direct);
    }

    /// <summary>
    /// The console's first-lane value of the readfirstlane at <paramref name="line"/> of <paramref name="site"/> when its
    /// value holds the mesh thread id from the prologue (<c>in_gid.x * N + in_gi</c>), written by no line on any path
    /// to it: the console wave's first thread's id; else null.
    /// </summary>
    private static string? ThreadId(TranslationState state, CodeBlock site, int line)
    {
        var name = FirstLanePattern().Match(site.Lines[line]).Groups["x"].Value;
        var prologue = state.Prologue.Select(text => ThreadIdPattern().Match(text)).FirstOrDefault(match => match.Success && match.Groups["name"].Value == name);
        var written = new Regex($@"(?<![\w.]){Regex.Escape(name)}\s*=(?!=)");

        if (prologue is null || site.Lines.Take(line).Any(written.IsMatch)
            || Before(state, site.Address).Any(address => state.Blocks.First(block => block.Address == address).Lines.Any(written.IsMatch)))
        {
            return null;
        }

        return $"(in_gid.x * {prologue.Groups["n"].Value}u + (in_gi & ~(VW_WAVE - 1u)))";
    }

    /// <summary>
    /// Whether every hardware wave of a console wave meets every exchange in the same round. A window falls a round behind
    /// only by skipping a region that holds a yield (after GS_ALLOC_REQ, <see cref="SkipRegion"/>), so no exchange may lie
    /// in a region windows decide for themselves (<see cref="WindowAnalysis.Divergent"/>) or after one holding a yield.
    /// </summary>
    public static bool Converged(TranslationState state, WindowAnalysis analysis)
    {
        var graph = new FlowGraph(state.Blocks);
        var behind = new HashSet<int>();
        var pending = new Stack<int>();

        foreach (var block in state.Blocks.Where(block => analysis.Divergent.Contains(block.Address) && block.Exit.Kind == BlockExitKind.Yield))
        {
            pending.Push(block.Address);
        }

        while (pending.Count > 0)
        {
            var address = pending.Pop();

            if (!behind.Add(address) || graph.NodeAt(address) is not int node)
            {
                continue;
            }

            foreach (var next in graph.Successors[node].Where(next => next != graph.End))
            {
                pending.Push(state.Blocks[next].Address);
            }

            if (state.Blocks[node].Exit.Kind == BlockExitKind.Yield && state.Blocks[node].Exit.Target is long resume)
            {
                pending.Push((int)resume);
            }
        }

        return !state.Blocks.Any(block => (analysis.Divergent.Contains(block.Address) || behind.Contains(block.Address))
            && block.Lines.Any(line => line.Contains("vw_first_put(", StringComparison.Ordinal)));
    }

    /// <summary>The blocks on a path to <paramref name="address"/> (it too, when a loop leads back to it).</summary>
    private static HashSet<int> Before(TranslationState state, int address)
    {
        var predecessors = state.Blocks.ToDictionary(block => block.Address, _ => new List<int>());

        foreach (var block in state.Blocks)
        {
            foreach (var target in new[] { block.Exit.Target, block.Exit.Next }.OfType<long>().Where(target => predecessors.ContainsKey((int)target)))
            {
                predecessors[(int)target].Add(block.Address);
            }
        }

        var seen = new HashSet<int>();
        var pending = new Stack<int>(predecessors[address]);

        while (pending.Count > 0)
        {
            var next = pending.Pop();

            if (seen.Add(next))
            {
                predecessors[next].ForEach(pending.Push);
            }
        }

        return seen;
    }

    /// <summary>The ordinals (among the block's readfirstlanes) of those under a full EXEC, before GS_ALLOC_REQ, of a value not equal on every lane.</summary>
    private static List<int> Qualifying(CodeBlock block, WindowState entry, int threads)
    {
        var state = entry.Clone();
        var transfer = new StatementTransfer(state, threads);
        var ordinals = new List<int>();
        var ordinal = 0;

        foreach (var line in block.Lines)
        {
            var first = FirstLanePattern().Match(line);

            if (first.Success)
            {
                var value = state.Get(first.Groups["x"].Value);

                if (state.Get(WindowNames.Active).Full && !state.Allocated && !state.PhaseTwo && !value.IsActiveUniform)
                {
                    ordinals.Add(ordinal);
                }

                ordinal++;
            }

            foreach (var statement in line.Split('\n'))
            {
                transfer.Apply(WindowStatement.Parse(statement));
            }
        }

        return ordinals;
    }

    [GeneratedRegex(@"^\s*[A-Za-z_]\w* = readfirstlane\((?<x>[A-Za-z_]\w*), active\);\s*$")]
    private static partial Regex FirstLanePattern();

    [GeneratedRegex(@"^\s*(?<name>[A-Za-z_]\w*) = \(in_gid\.x \* (?<n>\d{1,6})u \+ in_gi\);\s*$")]
    private static partial Regex ThreadIdPattern();
}
