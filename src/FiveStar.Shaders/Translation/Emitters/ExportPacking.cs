// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using FiveStar.Shaders.Disassembly;

namespace FiveStar.Shaders.Translation.Emitters;

/// <summary>
/// The channels of a compressed export (<c>exp ... compr</c>). The game's programs end in an FP16 export sequence
/// (<c>v_cvt_pkrtz_f16_f32</c> pairs, then a compressed export) whatever their targets, and the pipeline's
/// SPI_SHADER_COL_FORMAT decides what reaches the target: FP16_ABGR exports the halves, while a 32-bit export format
/// (32_R, 32_GR, 32_ABGR, from the pipeline cache) exports the values the pairs were packed from, as they are. The
/// integer targets take 32-bit formats: PS_VfxVBuffer packs 32-bit fields (a byte at bit 24 over 24-bit depth) for
/// an R32G32B32A32_UINT target exported 32_ABGR, and TL_PS_PBR_MATERIAL_STANDARD packs R9G9B9E5 light for its R32_UINT
/// target exported 32_R; neither survives a trip through FP16. So an integer target takes the pair's sources as bits
/// when they still hold them at the export, and a float target the halves. (shadPS4's export translation unpacks per
/// export format the same way; facts only.)
/// </summary>
internal static class ExportPacking
{
    private const string PackMnemonic = "v_cvt_pkrtz_f16_f32";
    private const string Suffix32 = "_e32";
    private const string Suffix64 = "_e64";
    private const string BranchPrefix = "s_branch";
    private const string ConditionalPrefix = "s_cbranch";
    private const int PackOperands = 3;
    private const int LowOperand = 1;
    private const int HighOperand = 2;

    /// <summary>A register name: its file letter, then its number.</summary>
    private const int RegisterNameLength = 2;

    /// <summary>
    /// One channel of the Exp float4 from the FP16 half in the low bits of <paramref name="half"/>: a float target's
    /// value, or for an integer target whose pair's sources are gone the half converted to an integer, as bits.
    /// </summary>
    public static string Channel(string half, string targetType)
    {
        var value = $"f16tof32({half})";

        return targetType switch
        {
            "uint" => $"asfloat((uint)round(max({value}, 0.0)))",
            "int" => $"asfloat((int)round({value}))",
            _ => value,
        };
    }

    /// <summary>
    /// The two operands <c>v_cvt_pkrtz_f16_f32</c> packed into <paramref name="register"/> before the export at
    /// <paramref name="address"/>, when nothing between (no write to them, no branch) changed them; null otherwise.
    /// </summary>
    public static (string Low, string High)? Sources(IReadOnlyList<Instruction> instructions, int address, string register)
    {
        int last = LastBefore(instructions, address);

        for (int index = last; index >= 0; index--)
        {
            var operands = Operands(instructions[index].Text);

            if (operands.Length == 0 || !Covers(operands[0], register))
            {
                continue;
            }

            if (Bare(instructions[index].Mnemonic) != PackMnemonic || operands.Length != PackOperands)
            {
                return null;
            }

            var (low, high) = (operands[LowOperand], operands[HighOperand]);

            if (!Plain(low) || !Plain(high) || !Unchanged(instructions, index + 1, last, low, high))
            {
                return null;
            }

            return (low, high);
        }

        return null;
    }

    /// <summary>Whether no instruction from <paramref name="first"/> to <paramref name="last"/> branches or writes either source.</summary>
    private static bool Unchanged(IReadOnlyList<Instruction> instructions, int first, int last, string low, string high)
    {
        for (int index = first; index <= last; index++)
        {
            var mnemonic = instructions[index].Mnemonic;

            if (mnemonic.StartsWith(BranchPrefix, StringComparison.Ordinal) || mnemonic.StartsWith(ConditionalPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            var operands = Operands(instructions[index].Text);

            if (operands.Length > 0 && (Covers(operands[0], low) || Covers(operands[0], high)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A register or constant without modifiers.</summary>
    private static bool Plain(string operand)
    {
        return operand.Length > 0 && operand.IndexOfAny(['|', ' ']) < 0 && operand[0] != '-' && !operand.StartsWith("abs", StringComparison.Ordinal);
    }

    /// <summary>Whether destination <paramref name="destination"/> (a register or a range such as v[2:5]) includes <paramref name="register"/>.</summary>
    private static bool Covers(string destination, string register)
    {
        if (destination == register)
        {
            return true;
        }

        int open = destination.IndexOf('[', StringComparison.Ordinal);
        int colon = destination.IndexOf(':', StringComparison.Ordinal);

        if (open <= 0 || colon < open || register.Length < RegisterNameLength || register[0] != destination[0] || !char.IsDigit(register[1]))
        {
            return false;
        }

        var first = int.Parse(destination[(open + 1)..colon], CultureInfo.InvariantCulture);
        var end = int.Parse(destination[(colon + 1)..destination.IndexOf(']', StringComparison.Ordinal)], CultureInfo.InvariantCulture);
        var number = int.Parse(register[1..], CultureInfo.InvariantCulture);

        return number >= first && number <= end;
    }

    /// <summary>The operands of an instruction's text, comma separated after the mnemonic.</summary>
    private static string[] Operands(string text)
    {
        int space = text.IndexOfAny([' ', '\t']);

        if (space < 0)
        {
            return [];
        }

        return text[(space + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static string Bare(string mnemonic)
    {
        if (mnemonic.EndsWith(Suffix32, StringComparison.Ordinal) || mnemonic.EndsWith(Suffix64, StringComparison.Ordinal))
        {
            return mnemonic[..^Suffix32.Length];
        }

        return mnemonic;
    }

    /// <summary>The index of the last instruction before <paramref name="address"/>.</summary>
    private static int LastBefore(IReadOnlyList<Instruction> instructions, int address)
    {
        int index = instructions.Count - 1;

        while (index >= 0 && instructions[index].Address >= address)
        {
            index--;
        }

        return index;
    }
}
