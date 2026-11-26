// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Tests.Cases;
using FiveStar.Shaders.Tests.Toolchain;
using FiveStar.Shaders.Translation.Stages;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// Runs of vector writes under EXEC become one branch over the lanes (Stages.ActiveRuns); the WARP case
/// exec_run_of_writes checks the values lane by lane.
/// </summary>
public sealed class ActiveRunsTests : IClassFixture<WarpFixture>
{
    private const string Run = """
          v1 = select(active, (uint)(v0 + 1u), v1);
          v2 = select(active, (uint)(v1 * 3u), v2);
          v3 = select(active, (uint)(asuint(asfloat(v2) + 1.0)), v3);
          v4 = select(active, (uint)(v3 ^ v1), v4);
        """;

    private readonly WarpFixture _fixture;

    public ActiveRunsTests(WarpFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Four_writes_under_exec_become_one_branch_in_their_order()
    {
        var result = ActiveRuns.Branch(Run);

        Assert.Equal("  if (active) {\n    v1 = (uint)(v0 + 1u);\n    v2 = (uint)(v1 * 3u);\n    v3 = (uint)(asuint(asfloat(v2) + 1.0));\n    v4 = (uint)(v3 ^ v1);\n  }", result);
    }

    [Theory]
    [InlineData("  v5 = select(active, (uint)(QuadReadAcrossX(v1)), v5);\n")]
    [InlineData("  v5 = select(active, (uint)(asuint(_tex1.Sample(_smp1, float2(0.0, 0.0)).x)), v5);\n")]
    [InlineData("  active = select(active, (bool)(v1 != 0u), active);\n")]
    [InlineData("  m_s0 = active && (v1 != 0u);\n")]
    public void A_write_reading_other_lanes_or_exec_ends_the_run(string breaker)
    {
        var lines = Run.Split('\n');
        var text = lines[0] + "\n" + lines[1] + "\n" + breaker + lines[2] + "\n" + lines[3] + "\n";

        Assert.Equal(text, ActiveRuns.Branch(text));
    }

    [WarpFact]
    public void The_pixel_case_runs_its_writes_in_one_branch()
    {
        var hlsl = new PixelCaseRunner(_fixture).Translate(PixelCases.Find("exec_run_of_writes"));

        Assert.Contains("if (active) {", hlsl, StringComparison.Ordinal);
    }
}
