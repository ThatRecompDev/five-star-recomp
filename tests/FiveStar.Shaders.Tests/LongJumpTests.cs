// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Disassembly;
using FiveStar.Shaders.Translation.Core;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// A long jump (s_getpc, s_add, s_addc, s_setpc) becomes an s_branch; when its SGPR pair and SCC are dead at the target,
/// its other three instructions become s_nop, so the program no longer reads its own code. The WARP case cf_long_jumps
/// (PixelControlFlowCases) runs such jumps.
/// </summary>
public sealed class LongJumpTests
{
    [Theory]
    [InlineData("s_mov_b32 s8, 0|s_mov_b32 s9, 0|s_cmp_eq_u32 s0, 0|s_endpgm", true)]
    [InlineData("s_endpgm", true)]
    [InlineData("s_load_dword s20, s[8:9], 0x0|s_endpgm", false)]
    [InlineData("s_mov_b32 s8, 0|s_add_u32 s20, s9, 1|s_endpgm", false)]
    [InlineData("s_cbranch_scc1 1 @T0x1c|s_endpgm|s_endpgm", false)]
    [InlineData("s_cmp_eq_u32 s0, 0|s_cbranch_scc1 1 @T0x24|s_mov_b64 s[8:9], 0|s_endpgm|s_add_u32 s20, s8, 0|s_endpgm", false)]
    public void A_long_jumps_pc_arithmetic_goes_when_nothing_reads_it(string target, bool dropped)
    {
        var instructions = new List<Instruction>
        {
            new(0, 4, "s_getpc_b64 s[8:9]"),
            new(4, 8, "s_add_u32 s8, s8, 16"),
            new(12, 4, "s_addc_u32 s9, s9, 0"),
            new(16, 4, "s_setpc_b64 s[8:9]"),
        };
        var address = 20;

        foreach (var text in target.Split('|'))
        {
            instructions.Add(new Instruction(address, 4, text));
            address += 4;
        }

        LongJumpRewriter.Rewrite(instructions);

        Assert.StartsWith("s_branch 0", instructions[3].Text, StringComparison.Ordinal);
        Assert.Equal(dropped, instructions.Take(3).All(instruction => instruction.Text == "s_nop 0"));
    }
}
