// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Containers;
using FiveStar.Shaders.Disassembly;
using FiveStar.Shaders.Hardware;
using FiveStar.Shaders.Translation;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// Cross-lane reads that see running lanes only (rdna_live_lanes.hlsli): a pixel program, whose part-full waves leave
/// lanes unlaunched, and a compute group no multiple of 32 read through vw_read_live; a group of whole waves keeps
/// vw_read and the prelude alone. WARP cannot show the difference (its 4-lane waves fill every pixel quad with helpers,
/// and it runs a part-full group's missing lanes), so this checks what the translator writes.
/// </summary>
public sealed class LiveLaneReadsTests
{
    private static readonly string[] Program =
    [
        "v_min_u32_dpp v0, v0, v0 quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xf",
        "v_readlane_b32 s1, v0, 0",
        "v_mov_b32 v20, s1",
        "s_endpgm",
    ];

    [Fact]
    public void Pixel_programs_and_part_full_groups_read_only_running_lanes()
    {
        string pixel = Translate(new ShaderConfig { Stage = HardwareStage.Pixel, WaveSize = 32 }, ShaderStage.Pixel);
        string whole = Translate(Compute(64), ShaderStage.Compute);
        string part = Translate(Compute(6), ShaderStage.Compute);

        Assert.Contains("vw_read_live(v0,", pixel, StringComparison.Ordinal);
        Assert.Contains("bool vw_live(uint lane)", pixel, StringComparison.Ordinal);
        Assert.DoesNotContain(" vw_read(", pixel, StringComparison.Ordinal);
        Assert.Contains("vw_read_live(", part, StringComparison.Ordinal);
        Assert.DoesNotContain("vw_read_live(", whole, StringComparison.Ordinal);
        Assert.DoesNotContain("bool vw_live(uint lane)", whole, StringComparison.Ordinal);
    }

    private static ShaderConfig Compute(int threads)
    {
        var compute = new ComputeConfig { Rsrc2 = 0, ScratchEnable = false, GroupSizeEnable = false, ThreadIdComponentCount = 1, LdsBlocks = 0, NumThreads = new ThreadGroupSize(threads, 1, 1), GroupIdSgprs = [] };

        return new ShaderConfig { Stage = HardwareStage.Compute, WaveSize = 32, UserSgprs = 12, Compute = compute };
    }

    private static string Translate(ShaderConfig config, ShaderStage stage)
    {
        var instructions = Program.Select((text, index) => new Instruction(index * 8, 8, text)).ToList();
        var program = new ShaderProgram(stage, instructions, new byte[instructions.Count * 8], ShaderSignatures.None, config, null);
        var options = new TranslationOptions(StructuredControlFlow: true, PinWaveSize: false, EmulateWaveSize: true);

        return HlslTranslator.Translate(program, options).Hlsl;
    }
}
