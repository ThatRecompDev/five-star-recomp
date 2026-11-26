// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Tests.Cases;
using FiveStar.Shaders.Tests.Toolchain;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// An NGG program through the two-phase mesh path, drawn natively and through the mesh-to-compute fallback (descriptor
/// table, root descriptors, and an overflowing dispatch whose extra group must be dropped). A variant whose regions
/// WARP's 4-lane waves skip while the 64-lane console wave runs them is classified as right on 4 lanes and draws so.
/// </summary>
public sealed class MeshShaderTests : IClassFixture<WarpFixture>
{
    private readonly WarpFixture _fixture;

    public MeshShaderTests(WarpFixture fixture)
    {
        _fixture = fixture;
    }

    public static TheoryData<string> Variants => [.. MeshStageRunner.Variants];

    [WarpTheory]
    [MemberData(nameof(Variants))]
    public void A_mesh_program_draws_the_reference_image(string variant)
    {
        var mismatches = new MeshStageRunner(_fixture).Run(variant);

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} mismatches:\n{string.Join("\n", mismatches.Take(16))}");
    }

    [WarpTheory]
    [InlineData(MeshStageRunner.Native)]
    [InlineData(MeshStageRunner.Supersteps)]
    [InlineData(MeshStageRunner.SuperstepCarry)]
    [InlineData(MeshStageRunner.Payload)]
    public void The_dxil_backend_compiles_the_mesh_program(string variant)
    {
        var mismatches = new MeshStageRunner(_fixture).Run(variant, direct: true);

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} mismatches:\n{string.Join("\n", mismatches.Take(16))}");
    }

    [WarpFact]
    public void A_mesh_program_that_skips_regions_draws_the_reference_image_on_narrower_waves()
    {
        var runner = new MeshStageRunner(_fixture);

        Assert.StartsWith("// program wave 64; console results on hardware waves >= 4 lanes\n", runner.Translate(MeshStageRunner.Windows), StringComparison.Ordinal);

        var mismatches = runner.Run(MeshStageRunner.Windows);

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} mismatches:\n{string.Join("\n", mismatches.Take(16))}");
    }

    [WarpTheory]
    [InlineData(MeshStageRunner.FirstLane)]
    [InlineData(MeshStageRunner.FirstLaneSplit)]
    public void Counts_read_from_the_first_lane_are_exchanged_so_the_program_runs_on_narrower_waves(string variant)
    {
        var runner = new MeshStageRunner(_fixture);
        var hlsl = runner.Translate(variant);

        Assert.StartsWith("// program wave 64; console results on hardware waves >= 4 lanes\n", hlsl, StringComparison.Ordinal);
        Assert.Contains("vw_first_put(0u, ms_step, v41)", hlsl, StringComparison.Ordinal);
        Assert.DoesNotContain("vw_first_put(1u", hlsl, StringComparison.Ordinal);
        Assert.Contains("s51 = (in_gid.x * 64u + (in_gi & ~(VW_WAVE - 1u)));", hlsl, StringComparison.Ordinal);

        var mismatches = runner.Run(variant);

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} mismatches:\n{string.Join("\n", mismatches.Take(16))}");
    }

    [WarpTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_readfirstlane_of_a_value_every_lane_holds_is_the_value_in_the_native_form(bool direct)
    {
        var runner = new MeshStageRunner(_fixture);
        var hlsl = runner.Translate(MeshStageRunner.UniformFirstLane);

        Assert.StartsWith("// program wave 64; console results on hardware waves >= 4 lanes\n", hlsl, StringComparison.Ordinal);
        Assert.Contains("s50 = v41;", hlsl, StringComparison.Ordinal);
        Assert.DoesNotContain("readfirstlane(", hlsl, StringComparison.Ordinal);

        var mismatches = runner.Run(MeshStageRunner.UniformFirstLane, direct);

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} mismatches:\n{string.Join("\n", mismatches.Take(16))}");
    }

    [WarpTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_mesh_group_of_two_console_waves_gets_console_results_in_supersteps(bool carry)
    {
        var runner = new MeshSuperstepRunner(_fixture);

        Assert.StartsWith("// program wave 64; console results on hardware waves >= 64 lanes\n", runner.Translate(), StringComparison.Ordinal);
        Assert.StartsWith("// program wave 64; console results on hardware waves >= 64 lanes\n", runner.TranslateFast(), StringComparison.Ordinal);
        Assert.StartsWith("// program wave 64; console results on hardware waves >= 4 lanes\n", runner.Supersteps(carry), StringComparison.Ordinal);
        Assert.Equal(carry, runner.Supersteps(carry).Contains("// carry: ", StringComparison.Ordinal));
        Assert.Contains("vw_ss_put_values(", runner.Supersteps(carry), StringComparison.Ordinal);
        Assert.Contains("// superstep: 19 stops,", runner.Supersteps(carry).Split('\n').First(line => line.Contains("// superstep:", StringComparison.Ordinal)), StringComparison.Ordinal);

        var mismatches = runner.Run(carry);

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} mismatches:\n{string.Join("\n", mismatches.Take(16))}");
    }
}
