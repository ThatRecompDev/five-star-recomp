// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.RegularExpressions;
using FiveStar.Shaders.Disassembly;

namespace FiveStar.Shaders.Translation.Core;

/// <summary>
/// Whether what a long jump leaves behind (its SGPR pair holding the PC, and SCC from the s_addc/s_subb) is dead at its
/// target: every path from there writes each of them before reading it, or ends. The jump's s_getpc, s_add and s_addc
/// then compute nothing the program reads. A path the walk cannot follow (an indirect jump, an unknown instruction
/// shape, too many steps) counts as a read.
/// </summary>
internal static partial class DeadJumpRegisters
{
    private const int MaxSteps = 20000;
    private const int Scc = -1;

    // Instructions whose first operand they only write (v_cmp: its lane mask; v_cmpx names none).
    private static readonly string[] Writers =
    [
        "s_mov_", "s_movk_", "s_add_", "s_sub_", "s_and_", "s_or_", "s_xor_", "s_andn2_", "s_orn2_", "s_nand_", "s_nor_",
        "s_xnor_", "s_not_", "s_mul_", "s_lshl", "s_lshr_", "s_ashr_", "s_bfe_", "s_bfm_", "s_cselect_", "s_brev_", "s_min_",
        "s_max_", "s_abs_", "s_absdiff_", "s_ff0_", "s_ff1_", "s_flbit_", "s_bcnt", "s_sext_", "s_load_", "s_buffer_load_",
        "s_getpc_", "s_getreg_", "s_memtime", "s_memrealtime", "s_pack_", "s_quadmask_", "s_wqm_", "v_readfirstlane_", "v_cmp_",
        "v_readlane_",
    ];

    // SALU instructions that set SCC.
    private static readonly string[] SccWriters =
    [
        "s_cmp_", "s_cmpk_", "s_bitcmp", "s_add_", "s_sub_", "s_and_", "s_or_", "s_xor_", "s_andn2_", "s_orn2_", "s_nand_",
        "s_nor_", "s_xnor_", "s_not_", "s_lshl", "s_lshr_", "s_ashr_", "s_bfe_", "s_min_", "s_max_", "s_abs_", "s_absdiff_",
    ];

    private static readonly string[] SccReaders = ["s_cbranch_scc", "s_cselect_", "s_cmov", "s_addc_", "s_subb_"];

    /// <summary>Whether s<paramref name="low"/>, s<paramref name="low"/>+1 and SCC are dead at instruction <paramref name="start"/>.</summary>
    public static bool Dead(IReadOnlyList<Instruction> instructions, IReadOnlyDictionary<long, int> indexOf, int start, int low)
    {
        var pending = new Stack<(int Index, HashSet<int> Live)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        pending.Push((start, [low, low + 1, Scc]));

        for (var steps = 0; pending.Count > 0; steps++)
        {
            if (steps > MaxSteps)
            {
                return false;
            }

            var (index, live) = pending.Pop();

            if (index >= instructions.Count || !seen.Add($"{index}:{string.Join(',', live.Order())}"))
            {
                continue;
            }

            var instruction = instructions[index];
            var mnemonic = instruction.Mnemonic;

            if (Reads(instruction, live))
            {
                return false;
            }

            var remaining = new HashSet<int>(live.Except(Writes(instruction)));

            if (remaining.Count == 0 || mnemonic == "s_endpgm")
            {
                continue;
            }

            if (mnemonic.StartsWith("s_setpc", StringComparison.Ordinal) || mnemonic.StartsWith("s_swappc", StringComparison.Ordinal))
            {
                return false;
            }

            if (BranchTargets.IsBranch(mnemonic))
            {
                if (!indexOf.TryGetValue(BranchTargets.Target(instruction.Text), out var target))
                {
                    return false;
                }

                pending.Push((target, remaining));

                if (mnemonic == "s_branch")
                {
                    continue;
                }
            }

            pending.Push((index + 1, remaining));
        }

        return true;
    }

    private static bool Reads(Instruction instruction, HashSet<int> live)
    {
        var mnemonic = instruction.Mnemonic;

        if (live.Contains(Scc) && SccReaders.Any(prefix => mnemonic.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return true;
        }

        var operands = Operands(instruction.Text);
        var reads = Writer(mnemonic) ? operands.Skip(1) : operands;

        return reads.Any(operand => Registers(operand).Any(live.Contains));
    }

    private static IEnumerable<int> Writes(Instruction instruction)
    {
        var mnemonic = instruction.Mnemonic;
        var written = new List<int>();

        if (Writer(mnemonic))
        {
            written.AddRange(Operands(instruction.Text).Take(1).SelectMany(Registers));
        }

        if (SccWriters.Any(prefix => mnemonic.StartsWith(prefix, StringComparison.Ordinal)))
        {
            written.Add(Scc);
        }

        return written;
    }

    private static bool Writer(string mnemonic)
    {
        return Writers.Any(prefix => mnemonic.StartsWith(prefix, StringComparison.Ordinal)) && !mnemonic.StartsWith("s_addk", StringComparison.Ordinal)
            && !mnemonic.StartsWith("s_mulk", StringComparison.Ordinal);
    }

    private static List<string> Operands(string text)
    {
        var space = text.IndexOfAny([' ', '\t']);

        if (space < 0)
        {
            return [];
        }

        return [.. text[(space + 1)..].Split(',').Select(operand => operand.Trim())];
    }

    /// <summary>The SGPR numbers an operand names (<c>s5</c>, <c>s[4:7]</c>, or one inside an expression).</summary>
    private static IEnumerable<int> Registers(string operand)
    {
        foreach (Match match in RangePattern().Matches(operand))
        {
            var first = int.Parse(match.Groups["first"].Value, CultureInfo.InvariantCulture);
            var last = int.Parse(match.Groups["last"].Value, CultureInfo.InvariantCulture);

            for (var register = first; register <= last; register++)
            {
                yield return register;
            }
        }

        foreach (Match match in SinglePattern().Matches(operand))
        {
            yield return int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
        }
    }

    [GeneratedRegex(@"\bs\[(?<first>\d+):(?<last>\d+)\]")]
    private static partial Regex RangePattern();

    [GeneratedRegex(@"\bs(?<n>\d+)\b")]
    private static partial Regex SinglePattern();
}
