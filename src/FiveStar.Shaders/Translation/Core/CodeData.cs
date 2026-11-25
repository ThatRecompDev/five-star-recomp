// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.RegularExpressions;
using FiveStar.Shaders.Disassembly;

namespace FiveStar.Shaders.Translation.Core;

/// <summary>
/// Where the data a program reads through its s_getpc_b64 pointers starts: each s_getpc is followed by the s_add_u32
/// (or s_sub_u32) that moves its low half to a table (<c>s_getpc_b64 s[4:5]; s_add_u32 s4, s4, .Ltable-.Lpc</c>), and
/// the earliest of those tables is the start. A per-lane load through such a pointer indexes only the code words from
/// there (<c>g_data</c>): indexed per lane, the whole code array cost a driver 26 s and 1.9 GB to compile one
/// tiled-lighting pixel program, its 53 table words 14 s and 0.5 GB. Lanes read at or after the table they point to.
/// </summary>
internal static partial class CodeData
{
    /// <summary>
    /// The byte offset of the earliest table the s_getpc pointers of <paramref name="translated"/> are moved to; null when
    /// there is none or one s_getpc is used otherwise (its loads then index the whole code).
    /// </summary>
    public static int? Start(IReadOnlyCollection<Instruction> translated)
    {
        var at = new Dictionary<int, Instruction>();

        foreach (var instruction in translated)
        {
            at.TryAdd(instruction.Address, instruction);
        }

        int? start = null;

        foreach (var getpc in translated)
        {
            var pc = GetPcPattern().Match(getpc.Text);

            if (!pc.Success)
            {
                continue;
            }

            // s_getpc_b64 gives the address of the next instruction.
            int? table = null;

            if (at.TryGetValue(getpc.Next, out var next))
            {
                table = Table(next, pc.Groups["lo"].Value, getpc.Next);
            }

            if (table is null)
            {
                return null;
            }

            start = Math.Min(start ?? int.MaxValue, table.Value);
        }

        return start;
    }

    /// <summary>The address <paramref name="add"/> moves the pointer in s<paramref name="low"/> to, null when it does not.</summary>
    private static int? Table(Instruction add, string low, int pc)
    {
        var match = AddPattern().Match(add.Text);

        if (!match.Success || match.Groups["dst"].Value != low || match.Groups["src"].Value != low)
        {
            return null;
        }

        var text = match.Groups["imm"].Value;
        long offset = text.StartsWith(HexPrefix, StringComparison.Ordinal)
            ? long.Parse(text[HexPrefix.Length..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : long.Parse(text, CultureInfo.InvariantCulture);

        long table = match.Groups["op"].Value == "add" ? pc + offset : pc - offset;

        if (table is < 0 or > int.MaxValue)
        {
            return null;
        }

        return (int)table;
    }

    private const string HexPrefix = "0x";

    [GeneratedRegex(@"^s_getpc_b64 s\[(?<lo>\d+):(?<hi>\d+)\]")]
    private static partial Regex GetPcPattern();

    [GeneratedRegex(@"^s_(?<op>add|sub)_u32 s(?<dst>\d+), s(?<src>\d+), (?<imm>0x[0-9a-f]+|\d+)$")]
    private static partial Regex AddPattern();
}
