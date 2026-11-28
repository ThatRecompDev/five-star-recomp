// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// <c>--dump-frame</c>: the pipeline whose draw last wrote each render and depth target of one command list, noted at
/// the draw. A target read back later (the list binds others, a barrier moves it out, the list closes) is named by its
/// writer, not by whatever the list ran since: the game runs dispatches and other draws while a target stays bound
/// (the hair V-buffer stays bound under the simulation's dispatches). One recording thread per list.
/// </summary>
internal sealed class StageWriters
{
    private readonly Dictionary<nint, nint> _writers = [];

    /// <summary>A draw of <paramref name="pipeline"/> with <paramref name="resource"/> bound as a target.</summary>
    public void Wrote(nint resource, nint pipeline)
    {
        _writers[resource] = pipeline;
    }

    /// <summary>The pipeline that last drew into <paramref name="resource"/>; <paramref name="fallback"/> when none did (a UAV texture).</summary>
    public nint WriterOf(nint resource, nint fallback)
    {
        return _writers.GetValueOrDefault(resource, fallback);
    }

    /// <summary>The list was reset: its earlier recording wrote nothing of the next one.</summary>
    public void Clear()
    {
        _writers.Clear();
    }
}
