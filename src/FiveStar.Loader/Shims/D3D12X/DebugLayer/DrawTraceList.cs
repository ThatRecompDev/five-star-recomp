// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>What <see cref="DrawTrace"/> saw set on one command list since it was created.</summary>
internal sealed class DrawTraceList
{
    /// <summary>The render-target view handles of the last OMSetRenderTargets.</summary>
    public nint[] Targets { get; set; } = [];

    /// <summary>The depth-stencil view handle; 0 for none.</summary>
    public nint DepthStencil { get; set; }

    public string Viewport { get; set; } = "not set";

    public string Scissor { get; set; } = "not set";

    /// <summary>The vertex buffers as trace lines.</summary>
    public string VertexBuffers { get; set; } = "\n      no vertex buffers set";

    /// <summary>How often the list was reset (Reset, slots 10 and 139).</summary>
    public long Resets { get; set; }

    /// <summary>The next draw's first render target is read back (a traced draw with <c>--d3d12-probe-traced</c>).</summary>
    public bool ProbeNext { get; set; }

    /// <summary>The texture SRVs of the traced records are read back after the traced Dispatch just recorded.</summary>
    public bool ProbeDispatch { get; set; }

    /// <summary>The list recorded traced work since it was last submitted: its submission names the queue.</summary>
    public bool Traced { get; set; }

    /// <summary>The pipeline of the last draw, dispatch or ExecuteIndirect recorded (<c>--dump-frame</c>'s name for a target no draw wrote).</summary>
    public nint LastPipeline { get; set; }

    /// <summary>The pipeline that last drew into each target in the dumped frame (<c>--dump-frame</c> names passes by it).</summary>
    public StageWriters Writers { get; } = new();

    /// <summary>Work was recorded since the render targets were last set: leaving them ends a pass that wrote them.</summary>
    public bool Worked { get; set; }

    /// <summary>The last ExecuteIndirect recorded was a compute one: it renders into no target, whatever the list left bound.</summary>
    public bool IndirectCompute { get; set; }
}
