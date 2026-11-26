// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Tests.Cases;
using FiveStar.Shaders.Tests.Toolchain;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// The depth a pixel program leaves under its DB_SHADER_CONTROL: a depth export under CONSERVATIVE_Z_EXPORT keeps the
/// promise as a conservative depth semantic (so the PC's depth test may still reject before the shader) and lands
/// clamped to the rasterized depth; DEPTH_BEFORE_SHADER writes the depth of a pixel the program kills, as on the console.
/// </summary>
public sealed class PixelDepthTests : IClassFixture<WarpFixture>
{
    // Z_EXPORT_ENABLE, KILL_ENABLE and CONSERVATIVE_Z_EXPORT = 1 (less than Z): the OctaSLOD impostors' control (with RE_Z).
    private const uint LessThanZ = 0x2041;
    private const uint GreaterThanZ = 0x4041;
    private const uint PlainZ = 0x41;

    // KILL_ENABLE with and without DEPTH_BEFORE_SHADER.
    private const uint KillLate = 0x40;
    private const uint KillEarly = 0x1040;

    private const float Rasterized = 0.5f;
    private const float Cleared = 1.0f;
    private const float Step = 0.125f;

    // mrtz = pos_x * 0.125 (0.0625 .. 0.9375 across the 8x8 target, the triangle sits at z 0.5); mrt0 = 7.
    private const string ExportProgram = """
        v_mul_f32 v1, 0x3e000000, v2
        v_mov_b32 v20, 7
        v_mov_b32 v21, 7
        v_mov_b32 v22, 7
        v_mov_b32 v23, 7
        exp mrtz v1, off, off, off
        {EXPORT}
        s_endpgm
        """;

    // Kills the pixels of odd columns (EXEC off at the done export); the others write mrt0 = 7.
    private const string KillProgram = """
        v_cvt_u32_f32 v1, v2
        v_and_b32 v1, 1, v1
        v_mov_b32 v20, 7
        v_mov_b32 v21, 7
        v_mov_b32 v22, 7
        v_mov_b32 v23, 7
        v_cmpx_eq_u32 0, v1
        {EXPORT}
        s_endpgm
        """;

    private readonly WarpFixture _fixture;

    public PixelDepthTests(WarpFixture fixture)
    {
        _fixture = fixture;
    }

    [WarpTheory]
    [InlineData(LessThanZ, "SV_DepthLessEqual", false)]
    [InlineData(LessThanZ, "SV_DepthLessEqual", true)]
    [InlineData(GreaterThanZ, "SV_DepthGreaterEqual", false)]
    [InlineData(PlainZ, "SV_Depth;", false)]
    public void A_conservative_depth_export_lands_clamped_to_the_rasterized_depth(uint control, string semantic, bool direct)
    {
        var testCase = new PixelCase("depth_export", ExportProgram, 4, _ => [7, 7, 7, 7]) { Raw = true, DbShaderControl = control };

        var mismatches = Draw(testCase, semantic, direct, x => ExportedDepth(control, (x + 0.5f) * Step));

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Take(16)));
    }

    [WarpTheory]
    [InlineData(KillEarly, "[earlydepthstencil]", false)]
    [InlineData(KillEarly, "[earlydepthstencil]", true)]
    [InlineData(KillLate, "Out main(", false)]
    public void Depth_before_the_shader_is_written_for_a_killed_pixel(uint control, string entry, bool direct)
    {
        var testCase = new PixelCase("depth_kill", KillProgram, 4, _ => null) { Raw = true, DbShaderControl = control };
        var early = control == KillEarly;

        var mismatches = Draw(testCase, entry, direct, x => KilledDepth(early, x));

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Take(16)));
    }

    private static float ExportedDepth(uint control, float exported)
    {
        if (control == LessThanZ)
        {
            return Math.Min(exported, Rasterized);
        }

        if (control == GreaterThanZ)
        {
            return Math.Max(exported, Rasterized);
        }

        return exported;
    }

    private static float KilledDepth(bool early, int x)
    {
        if (early || x % 2 == 0)
        {
            return Rasterized;
        }

        return Cleared;
    }

    /// <summary>Checks the translation holds <paramref name="text"/>, draws and lists every pixel whose depth differs.</summary>
    private List<string> Draw(PixelCase testCase, string text, bool direct, Func<int, float> expected)
    {
        var runner = new PixelCaseRunner(_fixture);

        Assert.Contains(text, runner.Translate(testCase), StringComparison.Ordinal);

        var depth = runner.Draw(testCase, direct).Depth!;
        var mismatches = new List<string>();

        for (var y = 0; y < PixelCase.Size; y++)
        {
            for (var x = 0; x < PixelCase.Size; x++)
            {
                var actual = BitConverter.UInt32BitsToSingle(depth[(y * PixelCase.Size) + x]);

                if (actual != expected(x))
                {
                    mismatches.Add($"({x},{y}) depth {actual} expected {expected(x)}");
                }
            }
        }

        return mismatches;
    }
}
