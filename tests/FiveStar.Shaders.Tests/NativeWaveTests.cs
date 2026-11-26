// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Tests.Cases;
using FiveStar.Shaders.Tests.Toolchain;
using FiveStar.Shaders.Translation.Core;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// Wave64 pixel programs on WARP's 4-lane waves (Waves.NativeWaveCheck): one that only branches on its lanes runs on the
/// native wave operations; a lane read by number, a ballot read as a number and a lane number from mbcnt keep the
/// emulation. Each draws its console results.
/// </summary>
public sealed class NativeWaveTests : IClassFixture<WarpFixture>
{
    private const string Emulated = "#define VW_EMULATE 1";

    private readonly WarpFixture _fixture;

    public NativeWaveTests(WarpFixture fixture)
    {
        _fixture = fixture;
    }

    [WarpTheory]
    [InlineData("native_if64", false)]
    [InlineData("native_readlane64", true)]
    [InlineData("native_ballot64", true)]
    [InlineData("native_mbcnt64", true)]
    public void A_program_keeps_wave_emulation_only_when_it_reads_across_lanes(string name, bool emulated)
    {
        var testCase = Cases().Single(candidate => candidate.Name == name);
        var runner = new PixelCaseRunner(_fixture);
        var hlsl = runner.Translate(testCase);

        Assert.Equal(emulated, hlsl.Contains(Emulated, StringComparison.Ordinal));
        Assert.Equal(!emulated, hlsl.Contains(WaveOps.NativeLine, StringComparison.Ordinal));

        var mismatches = runner.Run(testCase);
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} mismatches:\n{string.Join("\n", mismatches.Take(16))}");
    }

    private static IEnumerable<PixelCase> Cases()
    {
        // s_and_saveexec + s_cbranch_execz + restore on x < 3, as cf_if in wave64.
        const string Branch = """
            v_cvt_u32_f32_e32 v10, v2
            v_mov_b32 v20, 0
            v_mov_b32 v21, 0
            v_cmp_gt_u32_e32 vcc, 3, v10
            s_and_saveexec_b64 s[10:11], vcc
            s_cbranch_execz .LBB_end
            v_mov_b32 v20, 1
            v_add_nc_u32_e32 v21, 100, v10
            .LBB_end:
            s_or_b64 exec, exec, s[10:11]
            v_add_nc_u32_e32 v22, 5, v20
            {EXPORT}
            s_endpgm
            """;
        const string ReadLane = "v_cvt_u32_f32_e32 v10, v2\nv_readlane_b32 s4, v10, 1\nv_mov_b32 v20, s4";
        const string Ballot = "v_cvt_u32_f32_e32 v10, v2\nv_cmp_gt_u32_e32 vcc, 3, v10\ns_bcnt1_i32_b64 s4, vcc\nv_mov_b32 v20, s4";
        const string LaneNumber = "v_mbcnt_lo_u32_b32 v20, -1, 0\nv_mbcnt_hi_u32_b32 v20, -1, v20";

        return
        [
            new PixelCase("native_if64", Branch, 3, IfResult) { Raw = true, WaveSize = 64 },
            new PixelCase("native_readlane64", ReadLane, 1, pixel => [pixel.WavePixel(1)?.X ?? 0]) { WaveSize = 64 },
            new PixelCase("native_ballot64", Ballot, 1, pixel => [Enumerable.Range(0, pixel.Wave.Count).Count(lane => pixel.WavePixel(lane)?.X < 3)]) { WaveSize = 64 },
            new PixelCase("native_mbcnt64", LaneNumber, 1, pixel => [pixel.Lane]) { WaveSize = 64 },
        ];
    }

    private static Expected[] IfResult(PixelContext pixel)
    {
        if (pixel.X < 3)
        {
            return [1, 100 + pixel.X, 6];
        }

        return [0, 0, 5];
    }
}
