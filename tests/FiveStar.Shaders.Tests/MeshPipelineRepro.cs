// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using FiveStar.Shaders.Tests.Toolchain;
using FiveStar.Shaders.Tests.Warp;
using Xunit;
using Xunit.Abstractions;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// Opt-in: runs a mesh pipeline the loader made (FIVESTAR_REPRO_MESH and FIVESTAR_REPRO_PIXEL, DXIL files from the
/// program cache) with DispatchMesh(FIVESTAR_REPRO_GROUPS, 1, 1) on zeroed inputs: the window (slots 0 and 1) and the
/// address map (slot 7) zero, user data zero. With FIVESTAR_TEST_ADAPTER=hardware it runs on the GPU and reports how
/// long the dispatch took, to tell a group that never finishes from one that is only slow. Skipped without the files.
/// </summary>
public sealed class MeshPipelineRepro : IClassFixture<WarpFixture>
{
    private const int WindowWords = 1 << 16;
    private const int Size = 64;

    private readonly WarpFixture _fixture;
    private readonly ITestOutputHelper _output;

    public MeshPipelineRepro(WarpFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [WarpFact]
    public void A_cached_mesh_pipeline_runs_to_its_end_on_zeroed_inputs()
    {
        string? mesh = Environment.GetEnvironmentVariable("FIVESTAR_REPRO_MESH");
        string? pixel = Environment.GetEnvironmentVariable("FIVESTAR_REPRO_PIXEL");

        if (mesh is null || pixel is null || !File.Exists(mesh) || !File.Exists(pixel))
        {
            return;
        }

        int groups = int.Parse(Environment.GetEnvironmentVariable("FIVESTAR_REPRO_GROUPS") ?? "4", System.Globalization.CultureInfo.InvariantCulture);
        var bindings = new Bindings
        {
            UserSgprs = new uint[32],
            Buffers =
            [
                new BufferResource(0, new uint[WindowWords], Writable: false),
                new BufferResource(1, new uint[WindowWords], Writable: true),
                new BufferResource(7, new uint[WindowWords], Writable: false),
            ],
        };

        var clock = Stopwatch.StartNew();
        _fixture.Runner.Mesh(new MeshJob(File.ReadAllBytes(mesh), File.ReadAllBytes(pixel), Framebuffer.Exact(1, Size, Size), bindings, groups));
        _output.WriteLine($"DispatchMesh({groups}, 1, 1) of {Path.GetFileName(mesh)}: {clock.Elapsed.TotalMilliseconds:F1} ms");
    }
}
