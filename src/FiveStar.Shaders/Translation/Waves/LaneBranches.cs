// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;
using FiveStar.Shaders.Translation.Core;

namespace FiveStar.Shaders.Translation.Waves;

/// <summary>
/// Turns branches on "any lane of a mask" into per-lane branches before the superstep form cuts the blocks: a
/// <c>vw_any</c> there is a group round, and most of a large program's rounds are such branches (an s_cbranch_execz
/// around a few guarded instructions). Each lane takes the path of its own bit when <see cref="WindowAnalysis.PerLaneBranches"/>
/// shows that a lane outside the mask leaves the paths as the console does, and neither path holds a step
/// (<see cref="SuperstepSplitter.Stops"/>), a native wave or quad operation, GS_ALLOC_REQ, a barrier or the program end:
/// the lanes of a console wave then meet again at the join before any step, so every step still sees the whole wave.
/// A branch inside such a path counts once it is per-lane itself.
/// </summary>
internal static partial class LaneBranches
{
    /// <summary>Rewrites the per-lane branches of <paramref name="blocks"/> in place; how many there were.</summary>
    public static int Localize(List<CodeBlock> blocks, IReadOnlyList<string> prologue, int threads)
    {
        var candidates = new WindowAnalysis(blocks, prologue, threads).PerLaneBranches();
        var byAddress = blocks.ToDictionary(block => block.Address);
        var chosen = new Dictionary<int, LaneBranch>();
        var changed = true;

        while (changed)
        {
            changed = false;

            foreach (var branch in candidates.Where(branch => !chosen.ContainsKey(branch.Address) && IsQuiet(branch, byAddress, chosen)))
            {
                chosen[branch.Address] = branch;
                changed = true;
            }
        }

        for (var index = 0; index < blocks.Count; index++)
        {
            if (chosen.TryGetValue(blocks[index].Address, out var branch))
            {
                blocks[index] = Rewrite(blocks[index], branch.Mask);
            }
        }

        return chosen.Count;
    }

    /// <summary>Whether the paths from the branch to its join hold nothing the lanes of a console wave must reach together.</summary>
    private static bool IsQuiet(LaneBranch branch, Dictionary<int, CodeBlock> blocks, Dictionary<int, LaneBranch> chosen)
    {
        if (branch.Join is not int join)
        {
            return true;
        }

        var seen = new HashSet<int>();
        var pending = new Stack<int>([branch.Some, branch.None]);

        while (pending.Count > 0)
        {
            var address = pending.Pop();

            if (address == join || !seen.Add(address))
            {
                continue;
            }

            if (!blocks.TryGetValue(address, out var block) || block.Exit.Kind is BlockExitKind.End or BlockExitKind.Yield)
            {
                return false;
            }

            var lines = block.Lines.AsEnumerable();

            if (chosen.ContainsKey(address))
            {
                lines = lines.Where(line => !IsBranchLine(line));
            }

            if (lines.Any(line => SuperstepSplitter.Stops(line) || NativePattern().IsMatch(line)))
            {
                return false;
            }

            Successors(block.Exit).ForEach(pending.Push);
        }

        return true;
    }

    private static List<int> Successors(BlockExit exit)
    {
        var successors = new List<int>();

        if (exit.Target is long target)
        {
            successors.Add((int)target);
        }

        if (exit.Kind == BlockExitKind.Conditional && exit.Next is long next)
        {
            successors.Add((int)next);
        }

        return successors;
    }

    /// <summary>The block with <c>vw_any(mask)</c> in its branch replaced by the lane's own bit.</summary>
    private static CodeBlock Rewrite(CodeBlock block, string mask)
    {
        var any = new Regex($@"\bvw_any\((?:\({Regex.Escape(mask)}\)|{Regex.Escape(mask)})\)");
        var lines = new List<string>(block.Lines);

        for (var index = 0; index < lines.Count; index++)
        {
            if (IsBranchLine(lines[index]))
            {
                lines[index] = any.Replace(lines[index], mask);
            }
        }

        return new CodeBlock(block.Address, lines, block.Exit with { Condition = any.Replace(block.Exit.Condition ?? string.Empty, mask) });
    }

    private static bool IsBranchLine(string line)
    {
        return line.TrimStart().StartsWith("pc = (", StringComparison.Ordinal);
    }

    // GS_ALLOC_REQ (WaveIsFirstLane), native wave intrinsics and quad operations read lanes outside the thread.
    [GeneratedRegex(@"\b(?:Wave\w*|Quad\w*|quad_perm)\s*\(")]
    private static partial Regex NativePattern();
}
