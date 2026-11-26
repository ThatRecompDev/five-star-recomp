// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using FiveStar.Graphics;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Shims.D3D12X.Pipelines;
using FiveStar.Loader.Tests.Support;
using FiveStar.Shaders.Hardware;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// The pipeline disk cache on D3D12 WARP: a pipeline one process built is loaded by the next at once, without a
/// stand-in, and segment files of another build are deleted.
/// </summary>
public sealed unsafe class D3D12XPipelineCacheTests
{
    private const string Compute = "groupshared uint g[8]; [numthreads(8, 1, 1)] void main(uint i : SV_GroupIndex) { g[i] = i; }";

    [DxcFact]
    public void A_pipeline_built_by_one_process_is_loaded_by_the_next_without_a_stand_in()
    {
        var folder = Directory.CreateTempSubdirectory("fivestar-psolib-");

        try
        {
            using var device = GpuDevice.CreateWarp(debugLayer: false);
            var (device2, root) = DesktopObjects(device.Device.NativePointer);
            var program = TranslatedProgram.Of("cs", HardwareStage.Compute, Compile(folder.FullName));
            var description = new ComputePipelineDescription(0, new ShaderSource(ShaderSourceKind.CacheCode, new byte[24]));

            var (first, firstSummary) = CreateOnce(folder.FullName, device2, root, program, description, TimeSpan.FromMinutes(1));
            var (second, secondSummary) = CreateOnce(folder.FullName, device2, root, program, description, TimeSpan.Zero);

            Assert.True(first, firstSummary);
            Assert.True(second, secondSummary);
            Assert.Contains("1 stored", firstSummary, StringComparison.Ordinal);
            Assert.Contains("1 loaded", secondSummary, StringComparison.Ordinal);
            DesktopDevice.Release(root);
            DesktopDevice.Release(device2);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [DxcFact]
    public void A_held_ray_tracing_pipeline_is_a_stand_in_at_once_never_a_failed_creation()
    {
        // With a minute's budget a buildable pipeline comes back built; a held one failed its build within the budget and
        // the game got 0x80004005, which stalled its loading (w66d to w68a). It gets a stand-in that never builds.
        const string Name = "sgd1_0c075372_t41_deferred_tiled~CS_LaserLightShadowRaytracing_c3ab3042_Wrapped";
        var folder = Directory.CreateTempSubdirectory("fivestar-psohold-");

        try
        {
            using var device = GpuDevice.CreateWarp(debugLayer: false);
            var (device2, root) = DesktopObjects(device.Device.NativePointer);
            RayTracingPrograms.Note(Name, "#include \"rdna_bvh.hlsli\"", folder.FullName);
            var program = TranslatedProgram.Of(Name, HardwareStage.Compute, Compile(folder.FullName));
            var description = new ComputePipelineDescription(0, new ShaderSource(ShaderSourceKind.CacheCode, new byte[24]));
            var log = new LoaderLog(new StringWriter(), null, false);

            var builds = new PipelineBuildQueue(1, PipelineBuildQueue.AnyTime);
            builds.Start();
            var standIns = new StandInPipelines(TimeSpan.FromMinutes(1), builds, builds, new OnceLog(log));
            var factory = new PipelineFactory(device2, root, new FixedProgramLibrary(program), new ProgramSignatures(folder.FullName), new OnceLog(log), null, standIns, null);
            var iid = DesktopDevice.PipelineStateIid;
            nint pipeline;

            Assert.Equal(0, factory.Compute(description, &iid, &pipeline));
            Assert.NotEqual(0, pipeline);
            Assert.Equal(0, standIns.Desktop(pipeline, out bool pending));
            Assert.True(pending);
            DesktopDevice.Release(root);
            DesktopDevice.Release(device2);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [DxcFact]
    public void With_no_draws_every_pipeline_is_a_stand_in_that_never_builds()
    {
        var folder = Directory.CreateTempSubdirectory("fivestar-nodraws-");

        try
        {
            using var device = GpuDevice.CreateWarp(debugLayer: false);
            var (device2, root) = DesktopObjects(device.Device.NativePointer);
            var program = TranslatedProgram.Of("cs", HardwareStage.Compute, Compile(folder.FullName));
            var description = new ComputePipelineDescription(0, new ShaderSource(ShaderSourceKind.CacheCode, new byte[24]));
            var log = new LoaderLog(new StringWriter(), null, false);

            var builds = new PipelineBuildQueue(1, PipelineBuildQueue.AnyTime);
            builds.Start();
            var standIns = new StandInPipelines(TimeSpan.FromMinutes(1), builds, builds, new OnceLog(log));
            var factory = new PipelineFactory(device2, root, new FixedProgramLibrary(program), new ProgramSignatures(folder.FullName), new OnceLog(log), null, standIns, null);
            var iid = DesktopDevice.PipelineStateIid;
            nint pipeline;

            SkippedPipelines.All = true;

            try
            {
                // A buildable pipeline with a minute's budget: without --no-draws it comes back built.
                Assert.Equal(0, factory.Compute(description, &iid, &pipeline));
            }
            finally
            {
                SkippedPipelines.All = false;
            }

            Assert.NotEqual(0, pipeline);
            Assert.Equal(0, standIns.Desktop(pipeline, out bool pending));
            Assert.True(pending);
            DesktopDevice.Release(root);
            DesktopDevice.Release(device2);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [DxcFact]
    public void A_journal_pipeline_prebuilt_at_device_creation_is_loaded_by_the_game_without_a_stand_in()
    {
        var folder = Directory.CreateTempSubdirectory("fivestar-prebuild-");

        try
        {
            using var device = GpuDevice.CreateWarp(debugLayer: false);
            var (device2, root) = DesktopObjects(device.Device.NativePointer);
            var program = TranslatedProgram.Of("cs", HardwareStage.Compute, Compile(folder.FullName));
            var description = new ComputePipelineDescription(0, new ShaderSource(ShaderSourceKind.CacheCode, new byte[24]));
            var target = new WarmTarget(0, null, description);

            var log = new LoaderLog(new StringWriter(), null, false);
            var warnings = new OnceLog(log);
            var builds = new PipelineBuildQueue(1, PipelineBuildQueue.AnyTime);
            builds.Start();

            var cache = PipelineLibraryCache.Open(folder.FullName, device2, log)!;
            Assert.True(cache.WaitForSegments(TimeSpan.FromMinutes(1)));
            var factory = new PipelineFactory(device2, root, new FixedProgramLibrary(program), new ProgramSignatures(folder.FullName), warnings, null, new StandInPipelines(TimeSpan.Zero, builds, builds, warnings), cache);
            var prebuild = new JournalPrebuild(factory, cache);

            var first = prebuild.Build(target);
            var again = prebuild.Build(target);
            cache.Dispose();

            // The game's next creation, with no time for a build, finds it cached.
            var (direct, summary) = CreateOnce(folder.FullName, device2, root, program, description, TimeSpan.Zero);

            Assert.Equal((PrebuildOutcome.Built, PrebuildOutcome.Cached), (first, again));
            Assert.True(direct, summary);
            DesktopDevice.Release(root);
            DesktopDevice.Release(device2);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [DxcFact]
    public void A_pipeline_another_process_builds_after_this_one_opened_the_cache_is_loaded_after_a_refresh()
    {
        // A warm-up run beside the game: the game opened the folder first, the warm-up's segment comes later.
        var folder = Directory.CreateTempSubdirectory("fivestar-psojoin-");

        try
        {
            using var device = GpuDevice.CreateWarp(debugLayer: false);
            var (device2, root) = DesktopObjects(device.Device.NativePointer);
            var program = TranslatedProgram.Of("cs", HardwareStage.Compute, Compile(folder.FullName));
            var description = new ComputePipelineDescription(0, new ShaderSource(ShaderSourceKind.CacheCode, new byte[24]));

            var log = new LoaderLog(new StringWriter(), null, false);
            var warnings = new OnceLog(log);
            var builds = new PipelineBuildQueue(1, PipelineBuildQueue.AnyTime);
            builds.Start();

            var game = PipelineLibraryCache.Open(folder.FullName, device2, log)!;
            Assert.True(game.WaitForSegments(TimeSpan.FromMinutes(1)));
            var recipe = new PipelineFactory(device2, root, new FixedProgramLibrary(program), new ProgramSignatures(folder.FullName), warnings, null, new StandInPipelines(TimeSpan.Zero, builds, builds, warnings), game)
                .PrepareCompute(description)!;
            var key = recipe.Stream.Key;
            recipe.Stream.Dispose();

            var (built, summary) = CreateOnce(folder.FullName, device2, root, program, description, TimeSpan.FromMinutes(1));
            bool before = game.Contains(key);
            game.Refresh();
            bool after = game.Contains(key);
            game.Dispose();

            Assert.True(built, summary);
            Assert.False(before);
            Assert.True(after);
            DesktopDevice.Release(root);
            DesktopDevice.Release(device2);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [DxcFact]
    public void A_built_pipeline_reaches_a_segment_file_within_seconds_without_a_clean_exit()
    {
        // A boot killed after a build: the next one (a second cache on the folder, the first never disposed meanwhile)
        // finds the pipeline. Segments were written once a minute, so a kill lost the boot's first minute of builds.
        var folder = Directory.CreateTempSubdirectory("fivestar-psoflush-");

        try
        {
            using var device = GpuDevice.CreateWarp(debugLayer: false);
            var (device2, root) = DesktopObjects(device.Device.NativePointer);
            var program = TranslatedProgram.Of("cs", HardwareStage.Compute, Compile(folder.FullName));
            var description = new ComputePipelineDescription(0, new ShaderSource(ShaderSourceKind.CacheCode, new byte[24]));
            var log = new LoaderLog(new StringWriter(), null, false);

            var (killed, factory) = CacheAndFactory(folder.FullName, device2, root, program, log);
            killed.StartSaving();
            Assert.Equal(PrebuildOutcome.Built, new JournalPrebuild(factory, killed).Build(new WarmTarget(0, null, description)));
            var key = Key(factory, description);
            var written = Stopwatch.StartNew();

            while (Directory.GetFiles(folder.FullName, "*" + PipelineLibraryFile.Extension).Length == 0 && written.Elapsed < TimeSpan.FromSeconds(30))
            {
                Thread.Sleep(100);
            }

            using (var next = PipelineLibraryCache.Open(folder.FullName, device2, log)!)
            {
                Assert.True(next.Contains(key), $"no segment after {written.Elapsed.TotalSeconds:F1} s");
            }

            Assert.True(written.Elapsed < TimeSpan.FromSeconds(15), $"written after {written.Elapsed.TotalSeconds:F1} s");
            killed.Dispose();
            DesktopDevice.Release(root);
            DesktopDevice.Release(device2);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [DxcFact]
    public void Journaled_pipelines_missing_from_the_cache_are_counted_and_followed_until_built()
    {
        var folder = Directory.CreateTempSubdirectory("fivestar-coverage-");

        try
        {
            using var device = GpuDevice.CreateWarp(debugLayer: false);
            var (device2, root) = DesktopObjects(device.Device.NativePointer);
            var program = TranslatedProgram.Of("cs", HardwareStage.Compute, Compile(folder.FullName));
            var description = new ComputePipelineDescription(0, new ShaderSource(ShaderSourceKind.CacheCode, new byte[24]));
            var target = new WarmTarget(0, null, description);
            var log = new LoaderLog(new StringWriter(), null, false);

            var (cache, factory) = CacheAndFactory(folder.FullName, device2, root, program, log);
            var coverage = new JournalCoverage(factory, cache, log);
            Assert.Equal(1, coverage.Check([target]));
            Assert.Equal((1, 0, 0), (coverage.Missing, coverage.Built, coverage.Unchecked));

            new JournalPrebuild(factory, cache).Build(target);
            Assert.Equal(1, coverage.Built);
            Assert.StartsWith("1 of 1 built and cached, 0 left", coverage.Progress(), StringComparison.Ordinal);
            cache.Dispose();
            DesktopDevice.Release(root);
            DesktopDevice.Release(device2);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_segment_of_another_build_is_deleted()
    {
        var folder = Directory.CreateTempSubdirectory("fivestar-psolib-");

        try
        {
            var foreign = Path.Combine(folder.FullName, "old" + PipelineLibraryFile.Extension);
            File.WriteAllBytes(foreign, new byte[0x100]);
            using var device = GpuDevice.CreateWarp(debugLayer: false);
            var (device2, root) = DesktopObjects(device.Device.NativePointer);

            using (PipelineLibraryCache.Open(folder.FullName, device2, new LoaderLog(new StringWriter(), null, false)))
            {
                Assert.False(File.Exists(foreign));
            }

            DesktopDevice.Release(root);
            DesktopDevice.Release(device2);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    /// <summary>
    /// One process's creation through a factory with the cache; true when the game got the desktop pipeline itself
    /// (a zero budget gives every build a stand-in, so only a cache hit returns the pipeline).
    /// </summary>
    private static (bool Direct, string Summary) CreateOnce(
        string folder, nint device2, nint root, TranslatedProgram program, ComputePipelineDescription description, TimeSpan budget)
    {
        var log = new LoaderLog(new StringWriter(), null, false);
        var warnings = new OnceLog(log);
        var builds = new PipelineBuildQueue(1, PipelineBuildQueue.AnyTime);
        builds.Start();

        var standIns = new StandInPipelines(budget, builds, builds, warnings);
        var cache = PipelineLibraryCache.Open(folder, device2, log)!;
        Assert.True(cache.WaitForSegments(TimeSpan.FromMinutes(1)));
        var factory = new PipelineFactory(device2, root, new FixedProgramLibrary(program), new ProgramSignatures(folder), warnings, null, standIns, cache);
        var iid = DesktopDevice.PipelineStateIid;
        nint pipeline;

        int hr = factory.Compute(description, &iid, &pipeline);
        var direct = hr == 0 && pipeline != 0 && standIns.Desktop(pipeline, out bool pending) == pipeline && !pending;

        if (direct)
        {
            DesktopDevice.Release(pipeline);
        }

        cache.Dispose();

        return (direct, cache.Summary);
    }

    /// <summary>A cache on <paramref name="folder"/> with its segments open, and a factory that builds into it.</summary>
    private static (PipelineLibraryCache Cache, PipelineFactory Factory) CacheAndFactory(string folder, nint device2, nint root, TranslatedProgram program, LoaderLog log)
    {
        var warnings = new OnceLog(log);
        var builds = new PipelineBuildQueue(1, PipelineBuildQueue.AnyTime);
        builds.Start();

        var cache = PipelineLibraryCache.Open(folder, device2, log)!;
        Assert.True(cache.WaitForSegments(TimeSpan.FromMinutes(1)));
        var factory = new PipelineFactory(device2, root, new FixedProgramLibrary(program), new ProgramSignatures(folder), warnings, null, new StandInPipelines(TimeSpan.Zero, builds, builds, warnings), cache);

        return (cache, factory);
    }

    private static string Key(PipelineFactory factory, ComputePipelineDescription description)
    {
        var recipe = factory.PrepareCompute(description)!;
        var key = recipe.Stream.Key;
        recipe.Stream.Dispose();

        return key;
    }

    private static (nint Device2, nint Root) DesktopObjects(nint device)
    {
        var iid = DesktopDevice.Device2Iid;
        nint device2, root;

        Assert.Equal(0, DesktopDevice.QueryInterface(device, &iid, &device2));
        Assert.Equal(0, BindlessRoot.Create(device, &root));

        return (device2, root);
    }

    private static byte[] Compile(string folder)
    {
        string input = Path.Combine(folder, "cs.hlsl");
        string output = Path.Combine(folder, "cs.dxil");
        File.WriteAllText(input, Compute);

        using var dxc = Process.Start(new ProcessStartInfo(PipelineFactAttribute.Dxc, ["-T", "cs_6_6", "-E", "main", "-Fo", output, input]) { RedirectStandardError = true })!;
        string errors = dxc.StandardError.ReadToEnd();
        dxc.WaitForExit();
        Assert.True(dxc.ExitCode == 0, errors);

        return File.ReadAllBytes(output);
    }
}
