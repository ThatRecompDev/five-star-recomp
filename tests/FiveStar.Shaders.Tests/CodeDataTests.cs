// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Disassembly;
using FiveStar.Shaders.Translation.Core;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>Where the tables a program reads through its s_getpc_b64 pointers start (per-lane code loads, g_data).</summary>
public sealed class CodeDataTests
{
    [Fact]
    public void The_tables_start_at_the_earliest_offset_added_to_a_getpc()
    {
        // As in a tiled-lighting pixel program: each getpc moves to the same table, here before code that follows it.
        var start = CodeData.Start(Instructions(
            "s_getpc_b64 s[4:5]",
            "s_add_u32 s4, s4, 0x20",
            "s_addc_u32 s5, s5, 0",
            "s_getpc_b64 s[2:3]",
            "s_add_u32 s2, s2, 16",
            "s_endpgm"));

        // getpc at 0 gives 8 (+0x20 = 0x28); getpc at 0x18 gives 0x20 (+16 = 0x30).
        Assert.Equal(0x28, start);
    }

    [Fact]
    public void A_getpc_moved_back_points_before_it()
    {
        var start = CodeData.Start(Instructions("s_nop 0", "s_nop 0", "s_getpc_b64 s[4:5]", "s_sub_u32 s4, s4, 0x10", "s_endpgm"));

        Assert.Equal(0x08, start);
    }

    [Fact]
    public void A_getpc_used_otherwise_leaves_the_tables_unknown()
    {
        // The pointer is copied before it moves: its loads may read anywhere in the code.
        Assert.Null(CodeData.Start(Instructions("s_getpc_b64 s[4:5]", "s_mov_b32 s6, s4", "s_add_u32 s6, s6, 0x20", "s_endpgm")));
        Assert.Null(CodeData.Start(Instructions("s_getpc_b64 s[4:5]", "s_add_u32 s4, s6, 0x20", "s_endpgm")));
        Assert.Null(CodeData.Start(Instructions("s_endpgm")));
    }

    private static List<Instruction> Instructions(params string[] lines)
    {
        return [.. lines.Select((text, index) => new Instruction(index * 8, 8, text))];
    }
}
