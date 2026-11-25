// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Pipelines;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// Draws under the console rectangle list (topology 17; docs/d3d12x-shim.md, Rectangle lists). The topology itself
/// becomes a triangle list; a draw under it binds the rectangle-list variant of the list's pipeline
/// (<see cref="RectanglePipelines"/>), whose geometry shader adds each rectangle's fourth corner. The variant stays
/// bound until the game sets another pipeline or topology. While the variant builds the draw is skipped; without one it
/// covers half of each rectangle.
/// </summary>
internal sealed unsafe class RectangleDraws
{
    /// <summary>ID3D12GraphicsCommandList::SetPipelineState in the desktop vtable (d3d12.h).</summary>
    private const int DesktopSetPipelineState = 25;

    /// <summary>A count line is logged every this many expanded draws (a power of two).</summary>
    private const long ReportInterval = 4096;

    private readonly LoaderLog _log;
    private long _expanded;
    private long _triangles;
    private long _pending;

    public RectangleDraws(LoaderLog log)
    {
        _log = log;
    }

    /// <summary>The variants; null until pipelines exist (no shader folders, or before the device).</summary>
    public RectanglePipelines? Pipelines { get; private set; }

    /// <summary>Rectangle-list draws drawn with the variant.</summary>
    public long Expanded => Interlocked.Read(ref _expanded);

    /// <summary>Rectangle-list draws drawn as triangles, for want of a variant.</summary>
    public long AsTriangles => Interlocked.Read(ref _triangles);

    /// <summary>Rectangle-list draws skipped while their variant was building.</summary>
    public long WhileBuilding => Interlocked.Read(ref _pending);

    public void Attach(RectanglePipelines pipelines)
    {
        Pipelines = pipelines;
    }

    /// <summary>The game set <paramref name="pipeline"/> (desktop) on the list.</summary>
    [HotPath]
    public void SetPipeline(XboxObject* list, nint pipeline)
    {
        var state = RectangleListState.Of(list);
        state->Pipeline = pipeline;
        state->VariantBound = 0;
    }

    /// <summary>The game set a console topology; leaving the rectangle list puts the game's pipeline back.</summary>
    [HotPath]
    public void SetTopology(XboxObject* list, uint topology)
    {
        var state = RectangleListState.Of(list);
        state->Topology = topology;

        if (state->VariantBound != 0 && topology != PrimitiveTopologies.RectangleList)
        {
            Bind(list, state->Pipeline);
            state->VariantBound = 0;
        }
    }

    /// <summary>Reset with the list's initial (desktop) pipeline.</summary>
    [HotPath]
    public void Reset(XboxObject* list, nint pipeline)
    {
        var state = RectangleListState.Of(list);
        *state = default;
        state->Pipeline = pipeline;
    }

    /// <summary>
    /// Before a draw: under the rectangle list, binds the variant and counts the draw. True when the draw is to be
    /// skipped: its variant is still building (off the game's threads), and half of each rectangle is not drawn instead.
    /// </summary>
    [HotPath]
    public bool BeforeDraw(XboxObject* list)
    {
        var state = RectangleListState.Of(list);

        if (state->Topology != PrimitiveTopologies.RectangleList)
        {
            return false;
        }

        if (state->VariantBound == 0 && BindVariant(list, state) == RectanglePipelines.Pending)
        {
            Interlocked.Increment(ref _pending);

            return true;
        }

        if (state->VariantBound == 0)
        {
            if (Interlocked.Increment(ref _triangles) == 1)
            {
                Warn(state->Pipeline);
            }

            return false;
        }

        if ((Interlocked.Increment(ref _expanded) & (ReportInterval - 1)) == 0)
        {
            Report();
        }

        return false;
    }

    /// <summary>Binds the variant when it is built; returns what <see cref="RectanglePipelines.Variant"/> gave (0 without pipelines).</summary>
    [HotPath]
    private nint BindVariant(XboxObject* list, RectangleListState* state)
    {
        var pipelines = Pipelines;

        if (pipelines is null || state->Pipeline == 0)
        {
            return 0;
        }

        nint variant = pipelines.Variant(state->Pipeline);

        if (variant != 0 && variant != RectanglePipelines.Pending)
        {
            Bind(list, variant);
            state->VariantBound = 1;
        }

        return variant;
    }

    [HotPath]
    private static void Bind(XboxObject* list, nint pipeline)
    {
        nint inner = list->Inner;
        ((delegate* unmanaged<nint, nint, void>)ComObject.Slot(inner, DesktopSetPipelineState))(inner, pipeline);
    }

    private void Warn(nint pipeline)
    {
        _log.Warn($"[d3d12_xs] rectangle-list draw with pipeline 0x{pipeline:x} has no rectangle-list variant: drawn as triangles (half of each rectangle)");
    }

    private void Report()
    {
        _log.Info($"  [d3d12_xs] rectangle lists: {Expanded} draws expanded, {AsTriangles} drawn as triangles, {WhileBuilding} skipped while their variant built");
    }
}
