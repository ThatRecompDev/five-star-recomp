// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.RegularExpressions;

namespace FiveStar.Shaders.Translation.Core;

/// <summary>
/// The f16 rounding of the MODE register (FP_ROUND bits 2-3: 0 nearest even, 3 toward zero). The game switches it to
/// toward zero with <c>s_setreg_imm32_b32 hwreg(HW_REG_MODE, 2, 2), 3</c> and back with 0 (Leonida counted 9,417 such
/// writes in 6,333 programs); <c>s_round_mode</c> sets all four FP_ROUND bits. While it is toward zero, an f16 result
/// rounds toward zero and a finite overflow gives 65504, not infinity. The mode is followed in program order (the
/// writes are literal); a write from an SGPR leaves it as it was.
/// </summary>
internal static partial class HalfRoundMode
{
    private const int HalfRoundShift = 2;
    private const uint HalfRoundMask = 3;
    private const uint TowardZero = 3;
    private const int WordBits = 32;

    private const string Nearest = "f32tof16(";
    private const string Truncating = "f32tof16_rtz(";

    /// <summary>Whether <paramref name="op"/> may write the rounding mode.</summary>
    public static bool Writes(string op)
    {
        return op is "s_setreg_imm32_b32" or "s_setreg_b32" or "s_round_mode";
    }

    /// <summary>Whether f16 results round toward zero after <paramref name="op"/>, given <paramref name="current"/>.</summary>
    public static bool After(string op, string operands, bool current)
    {
        if (op == "s_round_mode")
        {
            return ((Literal(operands.Trim()) >> HalfRoundShift) & HalfRoundMask) == TowardZero;
        }

        var match = ModeWritePattern().Match(operands);

        if (!match.Success || op != "s_setreg_imm32_b32")
        {
            return current;
        }

        int offset = match.Groups["offset"].Success ? int.Parse(match.Groups["offset"].Value, CultureInfo.InvariantCulture) : 0;
        int size = match.Groups["size"].Success ? int.Parse(match.Groups["size"].Value, CultureInfo.InvariantCulture) : WordBits;
        uint mode = current ? TowardZero << HalfRoundShift : 0;
        uint field = size >= WordBits ? uint.MaxValue : (1u << size) - 1;
        mode = (mode & ~(field << offset)) | ((Literal(match.Groups["value"].Value) & field) << offset);

        return ((mode >> HalfRoundShift) & HalfRoundMask) == TowardZero;
    }

    /// <summary>Makes the f16 conversions of the statements from <paramref name="start"/> on round toward zero.</summary>
    public static void ToZero(List<string> body, int start)
    {
        for (int line = start; line < body.Count; line++)
        {
            body[line] = body[line].Replace(Nearest, Truncating, StringComparison.Ordinal);
        }
    }

    private static uint Literal(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.Parse(text["0x".Length..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return (uint)int.Parse(text, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"hwreg\(HW_REG_MODE(?:,\s*(?<offset>\d+),\s*(?<size>\d+))?\)\s*,\s*(?<value>0x[0-9a-fA-F]+|-?\d+)")]
    private static partial Regex ModeWritePattern();
}
