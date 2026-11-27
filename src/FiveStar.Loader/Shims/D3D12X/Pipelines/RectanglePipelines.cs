// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using FiveStar.Loader.Native;
using FiveStar.Shaders.Compilation;
using FiveStar.Shaders.Containers;
using FiveStar.Shaders.Hardware;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// The rectangle-list variant of each graphics pipeline (docs/d3d12x-shim.md, Rectangle lists): the same pipeline with
/// the geometry shader of its vertex program's output signature added, read from <c>rect-gs/</c> of the translate
/// output (<c>fivestar shader rect-gs</c>; never compiled here). A variant is built off the game's threads, started by the first rectangle-list draw with
/// its pipeline; 0 stands for a pipeline that has none (mesh or geometry stage, no shader file, creation failed).
/// </summary>
internal sealed unsafe class RectanglePipelines
{
    /// <summary>D3D12_BLEND_DESC bytes a trace shows: the two flags and render target 0 (d3d12.h).</summary>
    private const int BlendTraceBytes = 48;

    /// <summary>Leading D3D12_RASTERIZER_DESC and D3D12_DEPTH_STENCIL_DESC1 bytes a trace shows (fill, cull; depth enable, write mask, function).</summary>
    private const int StateTraceBytes = 12;

    /// <summary>What <see cref="Variant"/> returns while the variant builds: its draws are skipped meanwhile.</summary>
    public const nint Pending = -1;

    private readonly nint _device2;
    private readonly nint _rootSignature;
    private readonly string _folder;
    private readonly IProgramLibrary _programs;
    private readonly OnceLog _warnings;
    private readonly PipelineBuildQueue _builds;
    private readonly ConcurrentDictionary<nint, RectangleRecipe> _recipes = new();
    private readonly ConcurrentDictionary<nint, nint> _variants = new();
    private readonly ConcurrentDictionary<nint, VariantBuild> _building = new();
    private readonly ConcurrentDictionary<string, byte[]?> _shaders = new(StringComparer.Ordinal);

    /// <param name="device2">A desktop ID3D12Device2 (the caller keeps it alive).</param>
    /// <param name="rootSignature">The root signature of translated programs.</param>
    /// <param name="folder">The compiled geometry shaders (<c>&lt;translated&gt;/rect-gs</c>).</param>
    /// <param name="programs">Where a variant's programs are read again by name.</param>
    /// <param name="warnings">Where missing shaders and failures are reported, once each.</param>
    /// <param name="builds">The build threads that create the variants, as urgent creations (a draw waits for each).</param>
    public RectanglePipelines(nint device2, nint rootSignature, string folder, IProgramLibrary programs, OnceLog warnings, PipelineBuildQueue builds)
    {
        _builds = builds;
        _device2 = device2;
        _rootSignature = rootSignature;
        _folder = folder;
        _programs = programs;
        _warnings = warnings;
    }

    /// <summary>
    /// Compiles a geometry shader source missing from the folder into the given DXIL path and returns it (null when it
    /// fails), for programs made on demand; null reads the folder only.
    /// </summary>
    public Func<string, string, byte[]?>? Compiler { get; init; }

    /// <summary>Drops what is kept of <paramref name="pipeline"/>, which is about to be released, and its variant.</summary>
    public void Forget(nint pipeline)
    {
        _recipes.TryRemove(pipeline, out _);

        if (_variants.TryRemove(pipeline, out var variant) && variant != 0 && variant != Pending)
        {
            DesktopDevice.Release(variant);
        }
    }

    /// <summary>Keeps how <paramref name="pipeline"/> was built; a variant of an earlier pipeline at that address is dropped.</summary>
    public void Remember(nint pipeline, GraphicsPipelineDescription description, IReadOnlyList<TranslatedProgram> programs)
    {
        if (_variants.TryRemove(pipeline, out var stale) && stale != 0)
        {
            DesktopDevice.Release(stale);
        }

        var vertex = programs.FirstOrDefault(program => program.Stage == HardwareStage.Vertex);

        if (vertex is null || programs.Any(program => program.Stage is HardwareStage.Ngg or HardwareStage.Geometry))
        {
            _recipes.TryRemove(pipeline, out _);

            return;
        }

        var kept = programs.Where(program => Derived(program.Name)).ToDictionary(program => program.Name, StringComparer.Ordinal);
        _recipes[pipeline] = new RectangleRecipe(description, [.. programs.Select(program => program.Name)], vertex.Name, kept);
    }

    /// <summary>
    /// A program made from another on demand (a linked producer or a trimmed pixel program), which the library cannot
    /// read again by name: without it, every tiled-lighting pipeline (VS_TiledLighting linked to its TL_PS) drew its
    /// rectangles as triangles, half of each tile.
    /// </summary>
    public static bool Derived(string name)
    {
        return name.Contains(".link-", StringComparison.Ordinal) || name.Contains(".trim", StringComparison.Ordinal);
    }

    /// <summary>The programs, input layout and target formats of a remembered pipeline, for traces; null when unknown.</summary>
    public string? Describe(nint pipeline)
    {
        if (!_recipes.TryGetValue(pipeline, out var recipe))
        {
            return null;
        }

        var description = recipe.Description;
        string programs = string.Join(" + ", recipe.Programs);
        string inputs = string.Join(", ", description.InputLayout.Select(e => $"{e.Semantic}{e.SemanticIndex} fmt {e.Format} slot {e.InputSlot} +{e.Offset}"));

        string blend = Convert.ToHexString(description.Blend, 0, Math.Min(description.Blend.Length, BlendTraceBytes));

        string raster = Convert.ToHexString(description.Rasterizer, 0, Math.Min(description.Rasterizer.Length, StateTraceBytes));
        string depth = Convert.ToHexString(description.DepthStencil);

        return $"{programs}; inputs [{inputs}]; targets [{string.Join(", ", description.RenderTargetFormats)}] depth {description.DepthStencilFormat}; blend {blend}; raster {raster}; depth state {depth}; sample mask 0x{description.SampleMask:x}, samples {description.SampleCount}.{description.SampleQuality}, raster all {Convert.ToHexString(description.Rasterizer)}";
    }

    /// <summary>
    /// The variant of a desktop pipeline; 0 when it has none; <see cref="Pending"/> while it builds. The first call queues
    /// the build on the pipeline build threads (urgent) and never waits for it: the variant of a tiled-lighting pipeline
    /// is a full compile of its pixel program, and built on the drawing game thread it held a frame's job for over 25 s,
    /// until the game's hang watchdog ended the run in the opening cutscene.
    /// </summary>
    [HotPath]
    public nint Variant(nint pipeline)
    {
        if (_variants.TryGetValue(pipeline, out var variant))
        {
            return variant;
        }

        if (_building.ContainsKey(pipeline))
        {
            return Pending;
        }

        return Start(pipeline);
    }

    /// <summary>The first call for a pipeline: 0 without a recipe, otherwise its build queued and <see cref="Pending"/>.</summary>
    private nint Start(nint pipeline)
    {
        if (!_recipes.TryGetValue(pipeline, out var recipe))
        {
            _variants.TryAdd(pipeline, 0);

            return _variants[pipeline];
        }

        var build = new VariantBuild(recipe);

        if (!_building.TryAdd(pipeline, build))
        {
            return Pending;
        }

        // A build that finished between the caller's two lookups left its variant: no second build.
        if (_variants.TryGetValue(pipeline, out var built))
        {
            _building.TryRemove(new KeyValuePair<nint, VariantBuild>(pipeline, build));
            build.Done.Set();

            return built;
        }

        _builds.Queue(() => Finish(pipeline, build), urgent: true);

        return Pending;
    }

    /// <summary>Waits until the variant of <paramref name="pipeline"/> is no longer building; false on timeout.</summary>
    public bool WaitBuilt(nint pipeline, TimeSpan timeout)
    {
        return !_building.TryGetValue(pipeline, out var build) || build.Done.Wait(timeout);
    }

    /// <summary>
    /// On a build thread: builds the variant within the compile budget, then keeps it unless the pipeline was remembered
    /// again since. A failure keeps 0 (drawn as triangles) and is reported once; an exception would end the build thread.
    /// </summary>
    private PipelineResult Finish(nint pipeline, VariantBuild build)
    {
        nint variant = 0;
        nint kept = 0;

        try
        {
            variant = Build(build.Recipe);
        }
        catch (Exception exception)
        {
            _warnings.Warn($"rect-throw-{build.Recipe.Vertex}", $"[d3d12_xs] rectangle-list pipeline for {build.Recipe.Vertex}: {exception.GetType().Name}: {exception.Message}; drawn as triangles");
        }
        finally
        {
            if (_recipes.TryGetValue(pipeline, out var current) && ReferenceEquals(current, build.Recipe) && _variants.TryAdd(pipeline, variant))
            {
                kept = variant;
                variant = 0;
            }

            if (variant != 0)
            {
                DesktopDevice.Release(variant);
            }

            _building.TryRemove(new KeyValuePair<nint, VariantBuild>(pipeline, build));
            build.Done.Set();
        }

        return new PipelineResult(HResult.Ok, kept);
    }

    private nint Build(RectangleRecipe recipe)
    {
        var programs = new List<TranslatedProgram>();

        foreach (var name in recipe.Programs)
        {
            var program = recipe.Kept.GetValueOrDefault(name) ?? _programs.Load(name);

            if (program is null)
            {
                _warnings.Warn($"rect-program-{name}", $"[d3d12_xs] rectangle-list pipeline: {name} is no longer loadable; drawn as triangles");

                return 0;
            }

            programs.Add(program);
        }

        var vertex = programs.First(program => program.Name == recipe.Vertex);
        var shader = Shader(vertex);

        if (shader is null)
        {
            return 0;
        }

        programs.Add(TranslatedProgram.Of(vertex.Name, HardwareStage.Geometry, shader));
        var iid = DesktopDevice.PipelineStateIid;
        nint variant = 0;
        long shaderBytes = programs.Sum(program => (long)program.Dxil.Length);
        int hr;
        var ticket = CompileBudget.Enter(shaderBytes);

        try
        {
            hr = DesktopPipelineBuilder.Graphics(_device2, _rootSignature, recipe.Description, programs, &iid, &variant);
        }
        finally
        {
            CompileBudget.Exit(ticket);
        }

        if (HResult.Failed(hr))
        {
            _warnings.Warn($"rect-create-{vertex.Name}", $"[d3d12_xs] rectangle-list pipeline for {vertex.Name}: {HResult.Format(hr)}; drawn as triangles");

            return 0;
        }

        QueueProgress.Name(variant, $"loader:rect-list {string.Join(" + ", recipe.Programs)}");

        return variant;
    }

    /// <summary>The compiled geometry shader for the vertex program's outputs, or null with a warning.</summary>
    private byte[]? Shader(TranslatedProgram vertex)
    {
        string source;

        try
        {
            var chunk = DxbcContainer.Parse(vertex.Dxil.ToArray()).Find(PsvSignatureParser.ChunkCode) ?? throw new InvalidDataException("no PSV0 chunk");
            source = RectangleGeometryShader.Source(PsvSignatureParser.Parse(chunk.Data.Span).Outputs);
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            _warnings.Warn($"rect-source-{vertex.Name}", $"[d3d12_xs] no rectangle-list geometry shader for {vertex.Name}: {exception.Message}");

            return null;
        }

        return _shaders.GetOrAdd(RectangleGeometryShader.FileName(source), file => Load(file, source));
    }

    private byte[]? Load(string file, string source)
    {
        var path = Path.Combine(_folder, file);

        if (File.Exists(path))
        {
            return File.ReadAllBytes(path);
        }

        if (Compiler is not null)
        {
            return Compiler(source, path);
        }

        _warnings.Warn($"rect-file-{file}", $"[d3d12_xs] {path} missing (fivestar shader rect-gs <translated>): its rectangle lists are drawn as triangles");

        return null;
    }
}
