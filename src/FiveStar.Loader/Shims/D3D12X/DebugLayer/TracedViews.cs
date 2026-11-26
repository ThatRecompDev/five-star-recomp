// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// The views the draw trace saw written, by desktop CPU handle: the text a trace prints and the texture a probe reads
/// back, kept as one entry so both name the same view. A buffer, null or unwrapped view written over a texture's
/// handle leaves no texture there (the probe once read the old one, which the trace no longer showed).
/// </summary>
internal sealed class TracedViews
{
    private readonly ConcurrentDictionary<nint, TracedView> _views = new();

    /// <summary>
    /// A view was written at <paramref name="handle"/>; <paramref name="resource"/> 0 when no texture can be read there,
    /// <paramref name="plane"/> the plane it views (1: a depth-stencil's stencil).
    /// </summary>
    public void Write(nint handle, string text, nint resource = 0, int home = 0, int plane = 0, ulong address = 0)
    {
        _views[handle] = new TracedView(text, resource, home, plane, address);
    }

    /// <summary>The console address of the game resource behind the view at <paramref name="handle"/>; 0 for none.</summary>
    public ulong Address(nint handle)
    {
        _views.TryGetValue(handle, out var view);

        return view.Address;
    }

    /// <summary>The text of the view at <paramref name="handle"/>.</summary>
    public bool TryText(nint handle, out string text)
    {
        bool known = _views.TryGetValue(handle, out var view);
        text = view.Text;

        return known;
    }

    /// <summary>The texture behind the view at <paramref name="handle"/>, its home state and viewed plane, if it is one.</summary>
    public bool TryTexture(nint handle, out (nint Resource, int Home, int Plane) texture)
    {
        bool known = _views.TryGetValue(handle, out var view) && view.Resource != 0;
        texture = (view.Resource, view.Home, view.Plane);

        return known;
    }

    private readonly record struct TracedView(string Text, nint Resource, int Home, int Plane, ulong Address);
}
