// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.Pipelines;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// Without --raytracing a pipeline whose programs trace rays stays a stand-in: known when its program is translated,
/// and in a later boot or from the compile host by the marker beside its DXIL; other pipelines are built.
/// </summary>
public sealed class RayTracingProgramsTests : IDisposable
{
    // What a program does is known process-wide by its name, and other tests make programs too: each test's names carry
    // a hash of their own, so no other test can have noted, marked or scanned them first.
    private readonly string _hash = Convert.ToHexStringLower(BitConverter.GetBytes(Random.Shared.Next()));
    private readonly string _folder = Directory.CreateTempSubdirectory("fivestar-rt-").FullName;

    private string Tracing => Program("indirect_diffuse_lighting~CS_ScreenProbes_TraceRays");

    private string Earlier => Program("shadowmask~CS_TraceShadowRays_Checkerboard");

    private string Plain => Program("PS_LinearDepthOpaque");

    [Fact]
    public void A_program_that_only_serves_ray_tracing_stays_a_stand_in_by_its_name()
    {
        Assert.True(RayTracingPrograms.Skips(Program("bvh_skinning~CSComputeSkinningDefaultVelocity"), _folder, scanner: null));
        Assert.True(RayTracingPrograms.Skips(Program("standard_doublesided~CS_RaytracingResolve"), _folder, scanner: null));
        Assert.True(RayTracingPrograms.Skips(Program("rtdirectionalshadows~CS_RTDistantShadowFinalUpsample"), _folder, scanner: null));
        Assert.False(RayTracingPrograms.Skips(Program("shadowmask~CS_ClearShadowMask"), _folder, scanner: null));
    }

    [Fact]
    public void A_pipeline_whose_programs_trace_rays_stays_a_stand_in()
    {
        // Its warning goes to the active runtime's log: one of its own, not a disposed test's (a closed writer threw).
        using var test = Support.TestRuntime.Create();

        RayTracingPrograms.Note(Tracing, "#include \"rdna_prelude.hlsli\"\n#include \"rdna_bvh.hlsli\"\nvoid main() {}", _folder);
        RayTracingPrograms.Note(Plain, "#include \"rdna_prelude.hlsli\"\nvoid main() {}", _folder);

        Assert.True(File.Exists(Path.Combine(_folder, Tracing + RayTracingPrograms.MarkerExtension)));
        Assert.True(RayTracingPrograms.Skips(Tracing, _folder, scanner: null));
        Assert.False(RayTracingPrograms.Skips($"{Plain}.trim2 + {Program("MSNC_VS_LinearDepthSkin")}", _folder, scanner: null));

        // A program made in an earlier boot (or by the compile host) has only its marker; it is read again once made.
        Assert.False(RayTracingPrograms.Skips(Earlier, _folder, scanner: null));
        File.WriteAllBytes(Path.Combine(_folder, Earlier + RayTracingPrograms.MarkerExtension), []);
        RayTracingPrograms.Refresh(Earlier);
        Assert.True(RayTracingPrograms.Skips($"{Plain}.trim2 + {Earlier}.link-0123", _folder, scanner: null));
    }

    [Fact]
    public void A_program_made_before_the_markers_is_read_from_its_console_code_before_a_build()
    {
        // Found compiled with no marker: a game thread's check (no scanner) builds it, the check before a build reads
        // its code once, marks it and holds it; the next boot reads the marker.
        string old = Program("deferred_tiled~CS_LaserLightShadowRaytracing");
        string lit = Program("TL_PS_PBR_MATERIAL_STANDARD");
        int scans = 0;

        bool? Scan(string program)
        {
            scans++;

            return program == old;
        }

        Assert.False(RayTracingPrograms.Skips(old, _folder, scanner: null));
        Assert.True(RayTracingPrograms.Skips(old, _folder, Scan));
        Assert.True(RayTracingPrograms.Skips(old, _folder, Scan));
        Assert.False(RayTracingPrograms.Skips(lit, _folder, Scan));

        Assert.Equal(2, scans);
        Assert.True(File.Exists(Path.Combine(_folder, old + RayTracingPrograms.MarkerExtension)));
        Assert.True(File.Exists(Path.Combine(_folder, lit + RayTracingPrograms.NoMarkerExtension)));
    }

    /// <summary>A program name as the game's, with this test's hash.</summary>
    private string Program(string what)
    {
        return $"sgd1_{_hash}_t41_{what}_{_hash}_Wrapped";
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }
}
