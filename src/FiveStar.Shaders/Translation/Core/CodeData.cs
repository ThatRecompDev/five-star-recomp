// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Disassembly;

namespace FiveStar.Shaders.Translation.Core;

/// <summary>
/// Where a program's embedded data starts: after the last translated instruction, where the compiler puts the constant
/// tables a program reads through s_getpc_b64 pointers (gaps between translated instructions are dead code a branch
/// skips, not data). A per-lane load through such a pointer indexes only these words (<c>g_data</c>): indexed per lane,
/// the whole code array cost a driver 26 s and 1.9 GB to compile one tiled-lighting pixel program, its 54 data words
/// 14 s and 0.5 GB.
/// </summary>
internal static class CodeData
{
    private const int DwordBytes = 4;

    /// <summary>The byte offset, dword aligned, right after the last of <paramref name="translated"/>; 0 for none.</summary>
    public static int Start(IEnumerable<Instruction> translated)
    {
        int end = translated.Select(instruction => instruction.Next).DefaultIfEmpty(0).Max();

        return (end + DwordBytes - 1) / DwordBytes * DwordBytes;
    }
}
