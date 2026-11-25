// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;
using FiveStar.Shaders.Translation.Core;

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
    /// <summary>The instructions to exchange (address to site), or none when the analysis passes without them or cannot use them.</summary>
    public static Dictionary<long, int> Sites(TranslationState state)
    {
        var sites = new Dictionary<long, int>();

        if (state.Stage != ShaderStage.Mesh || !state.MeshYield || !state.EmulateWaves || state.FastWaves || state.Supersteps)
        {
            return sites;
        }

        var analysis = new WindowAnalysis(state.Blocks, state.Prologue, state.MeshThreads);

        if (analysis.Check() is null)
        {
            return sites;
        }

        var entries = new WindowAnalysis(state.Blocks, state.Prologue, state.MeshThreads).EntryFacts();

        if (entries is null)
        {
            return sites;
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

            foreach (var ordinal in ordinals)
            {
                sites[reads[ordinal].Address] = sites.Count;
            }
        }

        return sites;
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
}
