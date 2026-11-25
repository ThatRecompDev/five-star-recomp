// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Shaders.Translation.Emitters;

/// <summary>Operand positions in llvm-objdump's operand order: destination first, then the sources.</summary>
internal static class Operands
{
    public const int Destination = 0;

    public const int Source0 = 1;

    public const int Source1 = 2;

    public const int Source2 = 3;

    /// <summary>The fourth listed operand: the carry-in of v_addc, the select of v_cndmask, the addend of v_mad_u64.</summary>
    public const int Source3 = 4;

    /// <summary>Low dword of a 64-bit register pair.</summary>
    public const int Low = 0;

    /// <summary>High dword of a 64-bit register pair.</summary>
    public const int High = 1;

    /// <summary>Dwords in a 64-bit register pair.</summary>
    public const int PairDwords = 2;

    /// <summary>
    /// Where src0 and src1 of a VOP1/VOP2/VOPC operation stand, by role: a compare's sources are its last two operands
    /// (v_cmpx names no destination), a carry operation's follow its carry-out (<c>v_add_co_ci_u32 v1, vcc, v2, v3, vcc</c>).
    /// </summary>
    public static (int Source0, int Source1) Sources(string operation, int count)
    {
        if (operation.StartsWith("v_cmp", StringComparison.Ordinal))
        {
            return (count - PairDwords, count - 1);
        }

        if (operation.Contains("_co_ci_", StringComparison.Ordinal))
        {
            return (Source1, Source2);
        }

        return (Source0, Source1);
    }
}
