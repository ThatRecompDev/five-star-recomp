// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;
using FiveStar.Shaders.Translation.Core;

namespace FiveStar.Shaders.Translation.Waves;

/// <summary>
/// Before the superstep form of a mesh or compute program cuts the blocks, replaces the wave operations whose result
/// every lane of the console wave already holds, so they take no group round (in the native mesh form, where each is
/// a ballot and a lane read, only the values: <see cref="RewriteValues"/>): <c>readfirstlane(x, active)</c> of a value equal on every lane, or on
/// every active lane while EXEC is full, is <c>x</c>; <c>vw_any(m)</c> of a mask equal on every lane (or <c>active</c>
/// and a uniform value while EXEC is full) is <c>m</c>. The facts are the window analysis's console-wave facts
/// (<see cref="WindowAnalysis.EntryFacts"/>). In TransformD-style mesh programs most branches test a scalar condition
/// as a mask (<c>s_cselect exec, 0</c>, <c>s_and vcc, exec</c>) and most readfirstlanes read values made of scalars.
/// </summary>
internal static partial class UniformWaveOperations
{
    private static readonly AsyncLocal<bool> Off = new();

    /// <summary>Whether translations on this flow leave the operations as they are (the differential tests' other side).</summary>
    internal static bool Disabled
    {
        get => Off.Value;
        set => Off.Value = value;
    }

    /// <summary>Rewrites <paramref name="blocks"/> in place; how many operations it replaced.</summary>
    public static int Rewrite(List<CodeBlock> blocks, IReadOnlyList<string> prologue, int threads)
    {
        if (Disabled)
        {
            return 0;
        }

        return Uniform(blocks, prologue, threads) + Skips(blocks, prologue, threads);
    }

    /// <summary>Only the readfirstlanes and anys of uniform values, branches left as they are; how many it replaced.</summary>
    public static int RewriteValues(List<CodeBlock> blocks, IReadOnlyList<string> prologue, int threads)
    {
        if (Disabled)
        {
            return 0;
        }

        return Uniform(blocks, prologue, threads);
    }

    /// <summary>
    /// A branch on "any active lane" (<c>[!]vw_any(active)</c>) whose path, run with every lane inactive, changes nothing
    /// read where the paths meet (and the other path likewise, when the console takes it), enters that path always: the
    /// console runs it whenever a lane is active, and with none active both give the same state at the join. Not a
    /// loop's exit test (the path coming back to the branch), which would never end, nor a path holding a loop: run with
    /// no lane of the console wave active, a loop whose exit reads lane values (a readlane of a register only active
    /// lanes advance) never ends either. vfx_triangleClip~CS_DefragHeap_ComputeRelocations entered such a loop always
    /// and ran to its round bound in every frame (w98, about 28 ms).
    /// </summary>
    private static int Skips(List<CodeBlock> blocks, IReadOnlyList<string> prologue, int threads)
    {
        var entries = new WindowAnalysis(blocks, prologue, threads).EntryFacts();

        if (entries is null)
        {
            return 0;
        }

        var graph = new WindowGraph(blocks);
        int replaced = 0;

        for (int index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            var exit = block.Exit;
            var any = ActiveAnyPattern().Match(exit.Condition ?? string.Empty);

            if (exit.Kind != BlockExitKind.Conditional || !any.Success || !entries.TryGetValue(block.Address, out var entry)
                || graph.Join(block.Address) is not int join || block.Lines.Count == 0 || !block.Lines[^1].Contains(exit.Condition!, StringComparison.Ordinal))
            {
                continue;
            }

            var state = entry.Clone();
            var transfer = new StatementTransfer(state, threads);

            foreach (var statement in block.Lines.SelectMany(line => line.Split('\n')))
            {
                transfer.Apply(WindowStatement.Parse(statement));
            }

            var negated = any.Groups["not"].Value.Length > 0;
            var (some, none) = negated ? ((int)exit.Next!.Value, (int)exit.Target!.Value) : ((int)exit.Target!.Value, (int)exit.Next!.Value);

            if (Returns(graph, some, block.Address, join) || Loops(graph, some, join)
                || new SkipRegion(graph, some, join, state, false).Check() is not null
                || new SkipRegion(graph, none, join, state, true).Check() is not null)
            {
                continue;
            }

            var always = negated ? "false" : "true";
            block.Lines[^1] = block.Lines[^1].Replace(exit.Condition!, always, StringComparison.Ordinal);
            blocks[index] = block with { Exit = exit with { Condition = always } };
            replaced++;
        }

        return replaced;
    }

    /// <summary>Whether the path from <paramref name="start"/> comes back to <paramref name="branch"/> before <paramref name="join"/>.</summary>
    private static bool Returns(WindowGraph graph, int start, int branch, int join)
    {
        var seen = new HashSet<int>();
        var pending = new Stack<int>([start]);

        while (pending.Count > 0)
        {
            var address = pending.Pop();

            if (address == branch)
            {
                return true;
            }

            if (address == join || !seen.Add(address))
            {
                continue;
            }

            graph.Find(address)?.Successors.ToList().ForEach(pending.Push);
        }

        return false;
    }

    /// <summary>Whether the path from <paramref name="start"/> to <paramref name="join"/> holds a loop: a block it reaches again before <paramref name="join"/>.</summary>
    private static bool Loops(WindowGraph graph, int start, int join)
    {
        var finished = new HashSet<int>();
        var open = new HashSet<int>();
        var pending = new Stack<(int Address, bool Leaving)>([(start, false)]);

        while (pending.Count > 0)
        {
            var (address, leaving) = pending.Pop();

            if (leaving)
            {
                open.Remove(address);
                finished.Add(address);

                continue;
            }

            if (address == join || finished.Contains(address))
            {
                continue;
            }

            if (!open.Add(address))
            {
                return true;
            }

            pending.Push((address, true));

            foreach (var successor in graph.Find(address)?.Successors ?? [])
            {
                if (open.Contains(successor))
                {
                    return true;
                }

                pending.Push((successor, false));
            }
        }

        return false;
    }

    private static int Uniform(List<CodeBlock> blocks, IReadOnlyList<string> prologue, int threads)
    {
        var entries = new WindowAnalysis(blocks, prologue, threads).EntryFacts();

        if (entries is null)
        {
            return 0;
        }

        int replaced = 0;

        for (int index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];

            if (!entries.TryGetValue(block.Address, out var entry))
            {
                continue;
            }

            var state = entry.Clone();
            var transfer = new StatementTransfer(state, threads);

            for (int line = 0; line < block.Lines.Count; line++)
            {
                var text = Replace(block.Lines[line], state, ref replaced);
                block.Lines[line] = text;

                foreach (var statement in text.Split('\n'))
                {
                    transfer.Apply(WindowStatement.Parse(statement));
                }
            }

            if (block.Exit.Condition is string condition)
            {
                int ignored = 0;
                blocks[index] = block with { Exit = block.Exit with { Condition = Replace(condition, state, ref ignored) } };
            }
        }

        return replaced;
    }

    private static string Replace(string text, WindowState state, ref int replaced)
    {
        var active = state.Get(WindowNames.Active);
        var first = FirstLanePattern().Match(text);

        if (first.Success)
        {
            var x = state.Get(first.Groups["x"].Value);

            if (x.AllUniform == ValueFact.AllBits || (active.Full && x.IsActiveUniform))
            {
                replaced++;

                return $"{first.Groups["indent"].Value}{first.Groups["target"].Value} = {first.Groups["x"].Value};";
            }
        }

        int count = 0;
        var result = AnyPattern().Replace(text, call =>
        {
            var mask = call.Groups["mask"].Value;
            var fact = state.Get(mask);
            var uniform = (fact.Kind == MaskKind.Uniform && mask != WindowNames.Active) || (fact.Kind == MaskKind.ActiveAnd && active.Full);

            if (!uniform)
            {
                return call.Value;
            }

            count++;

            return $"({mask})";
        });
        replaced += count;

        return result;
    }

    [GeneratedRegex(@"^(?<indent>\s*)(?<target>[A-Za-z_]\w*) = readfirstlane\((?<x>[A-Za-z_]\w*), active\);\s*$")]
    private static partial Regex FirstLanePattern();

    [GeneratedRegex(@"\bvw_any\((?<mask>[A-Za-z_]\w*)\)")]
    private static partial Regex AnyPattern();

    [GeneratedRegex(@"^\(?(?<not>!?)vw_any\(active\)\)?$")]
    private static partial Regex ActiveAnyPattern();
}
