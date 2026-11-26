// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Translation.Emitters;
using FiveStar.Shaders.Translation.Stages;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// A pixel program's s_wqm of its initial EXEC sets every lane the GPU runs, so it becomes <c>active = true</c>; after
/// any branch or EXEC write before it the quad mask stays (the WARP cases HelperExecWholeQuad and WholeQuadMode run both).
/// </summary>
public sealed class WholeQuadEntryTests
{
    private static readonly string Quad = $"  active = {SaluEmitter.QuadMask("active")};\n";

    [Fact]
    public void The_entrys_whole_quad_mode_of_the_live_pixels_is_every_lane()
    {
        var text = Program("  m_s6 = active;\n" + Quad);

        var result = ProgramEmitter.WholeQuadEntry(text);

        Assert.Contains("\n  active = true;\n", result, StringComparison.Ordinal);
        Assert.DoesNotContain("QuadReadAcrossX", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_body_that_ends_early_runs_its_entry_once_inside_do_while_false()
    {
        var text = Program("  do {\n" + Quad + "  if (v0 == 0u) break;\n  } while (false);\n");

        Assert.Contains("\n  active = true;\n", ProgramEmitter.WholeQuadEntry(text), StringComparison.Ordinal);
    }

    [Fact]
    public void Whole_quad_mode_at_the_top_of_a_loop_keeps_the_quad_mask()
    {
        var text = Program("  do {\n" + Quad + "  active = active && (v0 == 0u);\n  } while (vw_any(active));\n");

        Assert.Equal(text, ProgramEmitter.WholeQuadEntry(text));
    }

    [Theory]
    [InlineData("  active = active && (v0 == 0u);\n")]
    [InlineData("  if (vw_any(active)) {\n  v1 = 0u;\n  }\n")]
    [InlineData("  if (v0 == 0u) discard;\n")]
    public void Whole_quad_mode_after_an_exec_write_or_a_branch_keeps_the_quad_mask(string before)
    {
        var text = Program(before + Quad);

        Assert.Equal(text, ProgramEmitter.WholeQuadEntry(text));
    }

    private static string Program(string body)
    {
        return "struct Out { float4 c : SV_Target0; };\nOut main(float4 in_pos : SV_Position) {\n  Out o = (Out)0;\n  bool active = !IsHelperLane(), m_s6 = false;\n" + body + "  return o;\n}\n";
    }
}
