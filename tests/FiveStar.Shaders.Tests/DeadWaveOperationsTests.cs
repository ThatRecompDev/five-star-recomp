// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Containers;
using FiveStar.Shaders.Hardware;
using FiveStar.Shaders.Tests.Toolchain;
using FiveStar.Shaders.Translation;
using FiveStar.Shaders.Translation.Core;
using FiveStar.Shaders.Translation.Waves;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// Every form drops the wave operations whose result nothing reads (in the superstep form each is a group round, and
/// dxc keeps an unused one in the others): a ballot into an SGPR half overwritten before any read, and an scc write
/// overwritten or never read. The ones a later block, branch or mesh yield reads stay. The WARP cases of every form
/// (pixel, compute, mesh, probe) check that the programs still compute what the ISA model says.
/// </summary>
public sealed class DeadWaveOperationsTests : IClassFixture<WarpFixture>
{
    private readonly WarpFixture _fixture;

    public DeadWaveOperationsTests(WarpFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Only_the_wave_operations_a_later_read_needs_stay()
    {
        List<string> first =
        [
            "  uint _t1 = vw_ballot(m_s4).x;",
            "  s4 = _t1;",
            "  uint _t2 = vw_ballot(m_s4).y;",
            "  s5 = _t2;",
            "  scc = vw_any(m_s4);",
            "  uint _t3 = vw_ballot(m_s6).x;",
            "  s6 = _t3;",
            "  scc = vw_any(lane_bit(s6, s7));",
            "  s4 = 0x0u;",
            "  pc = 0x10;",
        ];
        List<string> second =
        [
            "  v1 = select(active, s5 + s4, v1);",
            "  pc = (scc) ? 0x20 : PC_END;",
        ];
        List<string> third = ["  pc = PC_END;"];
        var blocks = new List<CodeBlock>
        {
            new(0, first, new BlockExit(BlockExitKind.Jump, 0x10, null, null)),
            new(0x10, second, new BlockExit(BlockExitKind.Conditional, 0x20, 0x20, "scc")),
            new(0x20, third, new BlockExit(BlockExitKind.End, null, null, null)),
        };

        int removed = DeadWaveOperations.Remove(blocks, new HashSet<string>());

        // s4's ballot is overwritten; the first scc by the second; s6's ballot is read by the second scc, which the branch reads.
        Assert.Equal(3, removed);
        Assert.Equal(
            [
                "  uint _t2 = vw_ballot(m_s4).y;",
                "  s5 = _t2;",
                "  uint _t3 = vw_ballot(m_s6).x;",
                "  s6 = _t3;",
                "  scc = vw_any(lane_bit(s6, s7));",
                "  s4 = 0x0u;",
                "  pc = 0x10;",
            ],
            first);
    }

    [Fact]
    public void A_ballot_read_after_a_mesh_yield_stays()
    {
        List<string> first =
        [
            "  uint _t1 = vw_ballot(m_s4).y;",
            "  s5 = _t1;",
            "  uint _t2 = vw_ballot(m_s6).y;",
            "  s7 = _t2;",
            "  pc_resume = 0x10; pc = PC_YIELD;",
        ];
        List<string> second = ["  v1 = select(active, s5, v1);", "  s7 = 0x0u;", "  pc = PC_END;"];
        var blocks = new List<CodeBlock>
        {
            new(0, first, new BlockExit(BlockExitKind.Yield, 0x10, null, null)),
            new(0x10, second, new BlockExit(BlockExitKind.End, null, null, null)),
        };

        // The yield resumes at 0x10 with the registers kept: s5 is read there, s7 overwritten.
        Assert.Equal(2, DeadWaveOperations.Remove(blocks, new HashSet<string>()));
        Assert.Equal(["  uint _t1 = vw_ballot(m_s4).y;", "  s5 = _t1;", "  pc_resume = 0x10; pc = PC_YIELD;"], first);
    }

    [WarpFact]
    public void The_native_form_drops_the_ballots_nothing_reads()
    {
        // s_and_saveexec writes s[4:5] (two ballot halves) and the restore reads them as a mask, never as integers.
        const string Program = """
            s_mov_b64 s[6:7], exec
            v_cmp_gt_u32_e32 vcc, 3, v0
            s_and_saveexec_b64 s[4:5], vcc
            s_cbranch_execz 1
            v_mov_b32_e32 v1, 1
            s_mov_b64 exec, s[4:5]
            s_endpgm
            """;
        var assembled = _fixture.Workbench.Assemble("dead_ballots", Program, 64);
        var compute = new ComputeConfig
        {
            Rsrc2 = 0, ScratchEnable = false, GroupSizeEnable = false, ThreadIdComponentCount = 1, LdsBlocks = 0,
            NumThreads = new ThreadGroupSize(64, 1, 1), GroupIdSgprs = [],
        };
        var config = new ShaderConfig { Stage = HardwareStage.Compute, WaveSize = 64, Compute = compute };
        var program = new ShaderProgram(ShaderStage.Compute, assembled.Instructions, assembled.Code, ShaderSignatures.None, config, null);
        var hlsl = HlslTranslator.Translate(program, new TranslationOptions(StructuredControlFlow: true, PinWaveSize: true, EmulateWaveSize: true)).Hlsl;

        Assert.DoesNotContain("vw_ballot(", hlsl, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lanes_own_bit_of_a_ballot_is_read_from_the_mask_bool_until_either_is_written()
    {
        List<string> lines =
        [
            "  uint _t1 = vw_ballot(m_s4).x;",
            "  s4 = _t1;",
            "  uint _t2 = vw_ballot(m_s4).y;",
            "  s5 = _t2;",
            "  active = active && lane_bit(s4, s5);",
            "  m_s4 = active;",
            "  v1 = select(lane_bit(s4, s5), 1u, v1);",
            "  pc = PC_END;",
        ];
        var blocks = new List<CodeBlock> { new(0, lines, new BlockExit(BlockExitKind.End, null, null, null)) };

        Assert.Equal(1, BallotBits.Substitute(blocks));
        Assert.Equal("  active = active && (m_s4);", lines[4]);
        Assert.Equal("  v1 = select(lane_bit(s4, s5), 1u, v1);", lines[6]);
    }

    [Fact]
    public void A_wave32_lanes_own_bit_needs_only_the_low_half()
    {
        List<string> lines =
        [
            "  uint _t1 = vw_ballot(m_s8).x;",
            "  s8 = _t1;",
            "  if (active) v1 = (lane_bit(s8, 0u) ? v4 : v3);",
            "  pc = PC_END;",
        ];
        var blocks = new List<CodeBlock> { new(0, lines, new BlockExit(BlockExitKind.End, null, null, null)) };

        Assert.Equal(0, BallotBits.Substitute([.. blocks.Select(block => block with { Lines = [.. block.Lines] })]));
        Assert.Equal(1, BallotBits.Substitute(blocks, wave32: true));
        Assert.Equal("  if (active) v1 = ((m_s8) ? v4 : v3);", lines[2]);
    }
}
