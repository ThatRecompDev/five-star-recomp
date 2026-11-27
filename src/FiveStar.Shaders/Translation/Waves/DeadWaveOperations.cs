// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;
using FiveStar.Shaders.Translation.Core;
using FiveStar.Shaders.Translation.Structure;

namespace FiveStar.Shaders.Translation.Waves;

/// <summary>
/// Drops the wave operations whose result nothing reads, before the superstep form cuts the blocks: every wave operation
/// there is a group round. The translator writes each lane-mask result into its SGPR halves (a ballot per half) and
/// each SALU mask operation's scc (<c>vw_any</c>), and most of those are overwritten before any read:
/// MSNC_VS_LinearDepthSkin_f1f2249c stopped 611 times, 424 of them at a ballot into an SGPR half and 100 at an scc.
/// A ballot (<c>uint _t = vw_ballot(..).x;</c> with <c>reg = _t;</c>) or an scc write is dropped when the register is
/// dead after it, by liveness over the block graph; wave operations have no other effect, so the program computes the
/// same. Writes inside braces do not count as kills, and a block whose successors are unknown keeps every register live.
/// </summary>
internal static partial class DeadWaveOperations
{
    private const int MaxPasses = 8;
    private const string Scc = "scc";

    /// <summary>A ballot's temporary is named twice: where it is set and where it is copied to its SGPR half.</summary>
    private const int TempMentions = 2;

    /// <summary>
    /// Removes dead wave operations from <paramref name="blocks"/> in place; <paramref name="pinned"/> are read after the
    /// program. <paramref name="ownBits"/> first reads a lane's own ballot bit from its mask bool (<see cref="BallotBits"/>);
    /// pixel programs keep the ballot, since helper lanes, in EXEC on the console, take no part in an HLSL ballot.
    /// <paramref name="wave32"/>: the program's waves have 32 lanes (<see cref="BallotBits"/> reads the low half only).
    /// </summary>
    public static int Remove(List<CodeBlock> blocks, IReadOnlySet<string> pinned, bool ownBits = true, bool wave32 = false)
    {
        if (ownBits)
        {
            BallotBits.Substitute(blocks, wave32);
        }

        int removed = 0;

        for (int pass = 0; pass < MaxPasses; pass++)
        {
            int now = RemoveOnce(blocks, pinned);
            removed += now;

            if (now == 0)
            {
                break;
            }
        }

        return removed;
    }

    private static int RemoveOnce(List<CodeBlock> blocks, IReadOnlySet<string> pinned)
    {
        var ballots = blocks.Select(block => Ballots(block.Lines)).ToList();
        var names = Candidates(blocks, ballots);

        if (names.Count == 0)
        {
            return 0;
        }

        // The lines stay as they are until each block's sweep, after its walk: what the walks read of them is read once.
        var facts = blocks.Select((block, index) => LineFacts.Of(block.Lines, ballots[index], names)).ToList();
        var liveIn = LiveIn(blocks, facts, names, pinned);
        var graph = new FlowGraph(blocks);
        int removed = 0;

        for (int block = 0; block < blocks.Count; block++)
        {
            var live = LiveOut(graph, block, liveIn, names, pinned);
            removed += Sweep(blocks[block].Lines, facts[block], live);
        }

        return removed;
    }

    /// <summary>The registers a dead-candidate line writes: ballot targets and scc.</summary>
    private static HashSet<string> Candidates(List<CodeBlock> blocks, List<string?[]> ballotsPerBlock)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (int block = 0; block < blocks.Count; block++)
        {
            var lines = blocks[block].Lines;
            var ballots = ballotsPerBlock[block];

            for (int i = 0; i < lines.Count; i++)
            {
                if (ballots[i] is string target)
                {
                    names.Add(target);
                }
                else if (SccPattern().IsMatch(lines[i]))
                {
                    names.Add(Scc);
                }
            }
        }

        return names;
    }

    /// <summary>
    /// Per line, the register a ballot there writes through the next line, or null. The temporaries' mentions are counted
    /// once per block (the lines naming each); a regex per temporary over every line made this quadratic, and the
    /// liveness walks ask for every line of a block again on each pass.
    /// </summary>
    private static string?[] Ballots(List<string> lines)
    {
        var ballots = new string?[lines.Count];
        Dictionary<string, int>? mentions = null;

        for (int i = 0; i + 1 < lines.Count; i++)
        {
            var ballot = BallotPattern().Match(lines[i]);

            if (!ballot.Success)
            {
                continue;
            }

            var copy = CopyPattern().Match(lines[i + 1]);
            string temp = ballot.Groups["temp"].Value;
            mentions ??= TempLines(lines);

            // The temporary must have no reader but the copy.
            if (copy.Success && copy.Groups["value"].Value == temp && mentions.GetValueOrDefault(temp) == TempMentions)
            {
                ballots[i] = copy.Groups["name"].Value;
            }
        }

        return ballots;
    }

    /// <summary>Per temporary (<c>_t</c> and digits, a whole word), how many lines name it.</summary>
    private static Dictionary<string, int> TempLines(List<string> lines)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            seen.Clear();

            foreach (Match temp in TempPattern().Matches(line))
            {
                if (seen.Add(temp.Value))
                {
                    counts[temp.Value] = counts.GetValueOrDefault(temp.Value) + 1;
                }
            }
        }

        return counts;
    }

    private static List<HashSet<string>> LiveIn(List<CodeBlock> blocks, List<LineFacts> facts, HashSet<string> names, IReadOnlySet<string> pinned)
    {
        var graph = new FlowGraph(blocks);
        var liveIn = blocks.Select(_ => new HashSet<string>(StringComparer.Ordinal)).ToList();

        for (bool changed = true; changed;)
        {
            changed = false;

            for (int block = blocks.Count - 1; block >= 0; block--)
            {
                var live = LiveOut(graph, block, liveIn, names, pinned);
                Backward(facts[block], live, lines: null);

                if (!live.SetEquals(liveIn[block]))
                {
                    liveIn[block] = live;
                    changed = true;
                }
            }
        }

        return liveIn;
    }

    /// <summary>
    /// What is live after a block: its successors' live-in; at the program's end the pinned registers. A two-phase mesh
    /// yield (s_barrier) resumes at the next instruction with every register kept, so that block's live-in counts too.
    /// </summary>
    private static HashSet<string> LiveOut(FlowGraph graph, int block, List<HashSet<string>> liveIn, HashSet<string> names, IReadOnlySet<string> pinned)
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        var exit = graph.Blocks[block].Exit;

        if (exit.Kind == BlockExitKind.Yield)
        {
            if (exit.Target is long resume && graph.NodeAt(resume) is int next)
            {
                live.UnionWith(liveIn[next]);
            }
            else
            {
                live.UnionWith(names);
            }
        }

        foreach (int next in graph.Successors[block])
        {
            if (next == graph.End)
            {
                live.UnionWith(names.Where(pinned.Contains));
            }
            else
            {
                live.UnionWith(liveIn[next]);
            }
        }

        if (graph.Successors[block].Count == 0)
        {
            live.UnionWith(names);
        }

        // The structured form branches on the exit's condition, not on the block's pc line: what it reads is live.
        if (exit.Condition is string condition)
        {
            live.UnionWith(NamePattern().Matches(condition).Select(name => name.Value).Where(names.Contains));
        }

        return live;
    }

    /// <summary>Drops the block's dead ballots and scc writes (walking backward from <paramref name="live"/>); how many.</summary>
    private static int Sweep(List<string> lines, LineFacts facts, HashSet<string> live)
    {
        var dead = new List<int>();
        Backward(facts, live, dead);

        foreach (int index in dead.OrderDescending())
        {
            lines.RemoveAt(index);
        }

        return dead.Count;
    }

    /// <summary>
    /// Walks the lines backward from <paramref name="live"/>, leaving the live-in set in it; with <paramref name="lines"/>
    /// collects the indices of dead candidate lines (a ballot's two lines, an scc write).
    /// </summary>
    private static void Backward(LineFacts facts, HashSet<string> live, List<int>? lines)
    {
        for (int i = facts.Depths.Length - 1; i >= 0; i--)
        {
            if (i > 0 && facts.Depths[i - 1] == 0 && facts.Ballots[i - 1] is string target && !live.Contains(target))
            {
                lines?.AddRange([i - 1, i]);
                i--;

                continue;
            }

            if (facts.Depths[i] == 0 && facts.Scc[i] && !live.Contains(Scc))
            {
                lines?.Add(i);

                continue;
            }

            if (facts.Writes[i] is string write)
            {
                live.Remove(write);
            }

            live.UnionWith(facts.Reads[i]);
        }
    }

    /// <summary>
    /// What the backward walks read of a block's lines: brace depths, ballots, scc writes, the top-level plain write of
    /// each line and the candidate names it then reads (of the value when it writes, of the whole line otherwise).
    /// </summary>
    private sealed record LineFacts(int[] Depths, string?[] Ballots, bool[] Scc, string?[] Writes, string[][] Reads)
    {
        public static LineFacts Of(List<string> lines, string?[] ballots, HashSet<string> names)
        {
            var depths = DepthsOf(lines);
            var scc = new bool[lines.Count];
            var writes = new string?[lines.Count];
            var reads = new string[lines.Count][];
            var lookup = names.GetAlternateLookup<ReadOnlySpan<char>>();

            for (int i = 0; i < lines.Count; i++)
            {
                scc[i] = SccPattern().IsMatch(lines[i]);
                var write = depths[i] == 0 ? WritePattern().Match(lines[i]) : Match.Empty;
                string read = lines[i];

                if (write.Success)
                {
                    writes[i] = write.Groups["name"].Value;
                    read = write.Groups["value"].Value;
                }

                var found = new List<string>();

                foreach (var name in NamePattern().EnumerateMatches(read))
                {
                    if (lookup.TryGetValue(read.AsSpan(name.Index, name.Length), out var known))
                    {
                        found.Add(known);
                    }
                }

                reads[i] = [.. found];
            }

            return new LineFacts(depths, ballots, scc, writes, reads);
        }
    }

    /// <summary>The brace depth before each line (0 at the block's top level).</summary>
    private static int[] DepthsOf(List<string> lines)
    {
        var depths = new int[lines.Count];
        int depth = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            depths[i] = depth;
            depth += lines[i].Count(character => character == '{') - lines[i].Count(character => character == '}');
        }

        return depths;
    }

    [GeneratedRegex(@"^\s*uint (?<temp>_t\d+) = vw_ballot\([^;]*\)\.[xy];\s*$")]
    private static partial Regex BallotPattern();

    [GeneratedRegex(@"^\s*(?<name>[A-Za-z_]\w*) = (?<value>_t\d+);\s*$")]
    private static partial Regex CopyPattern();

    [GeneratedRegex(@"^\s*scc = vw_any\([^;]*\);\s*$")]
    private static partial Regex SccPattern();

    [GeneratedRegex(@"^\s*(?<name>[A-Za-z_]\w*)\s*=(?!=)\s*(?<value>[^;{}]*);\s*$")]
    private static partial Regex WritePattern();

    [GeneratedRegex(@"(?<![.\w])[A-Za-z_]\w*")]
    private static partial Regex NamePattern();

    // \b on both sides, as the ballot's temporary was once searched for: no word character before or after.
    [GeneratedRegex(@"(?<!\w)_t\d+(?!\w)")]
    private static partial Regex TempPattern();
}
