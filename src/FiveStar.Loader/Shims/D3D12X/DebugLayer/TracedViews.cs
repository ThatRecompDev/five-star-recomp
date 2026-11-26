// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using FiveStar.Loader.Shims.D3D12X.Resources;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// The views the draw trace saw written, by desktop CPU handle: the text a trace prints and the texture a probe reads
/// back, kept as one entry so both name the same view. A buffer, null or unwrapped view written over a texture's
/// handle leaves no texture there (the probe once read the old one, which the trace no longer showed).
/// <para>
/// A view names its texture by its raw desktop pointer, and the texture can go long before the view is written over:
/// w110 released 152k textures (retired or replaced by <see cref="GpuTextures"/>, the game's own Releases), and w111's
/// probe recorded a barrier on a freed one (an access violation in DrawProbes.Record). So a texture is only handed out
/// while it is alive: one of <see cref="GpuTextures"/> while its registry still holds it, a game resource until its last
/// Release (<see cref="Released"/>). No view takes a reference: millions of views would keep their textures alive.
/// </para>
/// </summary>
internal sealed class TracedViews
{
    private readonly ConcurrentDictionary<nint, TracedView> _views = new();

    /// <summary>The game resources a view names that have not had their last Release since.</summary>
    private readonly ConcurrentDictionary<nint, byte> _live = new();

    /// <summary>
    /// A view was written at <paramref name="handle"/>; <paramref name="resource"/> 0 when no texture can be read there,
    /// <paramref name="plane"/> the plane it views (1: a depth-stencil's stencil). <paramref name="texture"/> is the
    /// registry's texture when the view is of one (its liveness is the registry's); otherwise the resource is a game
    /// resource, alive until <see cref="Released"/>.
    /// </summary>
    public void Write(nint handle, string text, nint resource = 0, int home = 0, int plane = 0, ulong address = 0, GpuTexture? texture = null)
    {
        if (resource != 0 && texture is null)
        {
            _live[resource] = 0;
        }

        _views[handle] = new TracedView(text, resource, home, plane, address, texture);
    }

    /// <summary>A game resource had its last Release: views of it no longer hand it out.</summary>
    public void Released(nint resource)
    {
        _live.TryRemove(resource, out _);
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

    /// <summary>
    /// The texture behind the view at <paramref name="handle"/>, its home state and viewed plane, if it is one and still
    /// alive: a registry texture <paramref name="registered"/> still holds, a game resource not released since.
    /// </summary>
    public bool TryTexture(nint handle, out (nint Resource, int Home, int Plane) texture, Func<GpuTexture, bool>? registered = null)
    {
        bool known = _views.TryGetValue(handle, out var view) && view.Resource != 0 && Alive(view, registered);
        texture = (view.Resource, view.Home, view.Plane);

        return known;
    }

    private bool Alive(TracedView view, Func<GpuTexture, bool>? registered)
    {
        if (view.Texture is not null)
        {
            return registered is not null && registered(view.Texture);
        }

        return _live.ContainsKey(view.Resource);
    }

    private readonly record struct TracedView(string Text, nint Resource, int Home, int Plane, ulong Address, GpuTexture? Texture);
}
