// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// The desktop descriptor handles that hold an SRV or UAV of a registered texture: noted where a view is written (slot
/// 70, CreateShaderResourceView, CreateUnorderedAccessView) and where descriptors are copied (a copy holds what its
/// source held), forgotten where a handle is written anew (<see cref="XboxDescriptorHeaps.Destination(nint, Binding.BufferRecord)"/>).
/// Before a texture is released its views still held are nulled or written over its replacement
/// (<see cref="ReleasedTextureViews"/>), so a later read never reaches a released texture.
/// </summary>
internal sealed unsafe class TextureViewHandles
{
    /// <summary>D3D12_SRV_DIMENSION_TEXTURE2D (and D3D12_UAV_DIMENSION_TEXTURE2D): a view written without a desc.</summary>
    private const int WholeTextureView = 4;

    private const int DimensionDword = 1;

    private readonly ConcurrentDictionary<nint, HeldTextureView> _byHandle = new();
    private readonly ConcurrentDictionary<GpuTexture, ConcurrentDictionary<nint, byte>> _byTexture = new();
    private volatile bool _any;

    /// <summary>An SRV of <paramref name="texture"/> of SRV dimension <paramref name="dimension"/> was written at <paramref name="handle"/>, without a desc kept.</summary>
    public void Written(nint handle, GpuTexture texture, int dimension)
    {
        Held(handle, new HeldTextureView(texture, dimension, false, texture.Desktop, false, default));
    }

    /// <summary>
    /// A view of <paramref name="texture"/> naming the desktop <paramref name="target"/> was written at <paramref name="handle"/>
    /// with <paramref name="description"/> (an SRV or, when <paramref name="unordered"/>, a UAV desc; null for none).
    /// </summary>
    public void Written(nint handle, GpuTexture texture, nint target, void* description, bool unordered)
    {
        var kept = default(TextureViewDescription);
        int dimension = WholeTextureView;

        if (description != null)
        {
            new ReadOnlySpan<int>(description, TextureViewDescription.Dwords).CopyTo(kept);
            dimension = ((int*)description)[DimensionDword];
        }

        Held(handle, new HeldTextureView(texture, dimension, unordered, target, description != null, kept));
    }

    /// <summary>The view <paramref name="handle"/> holds; null for none this class knows.</summary>
    public HeldTextureView? At(nint handle)
    {
        if (_byHandle.TryGetValue(handle, out var held))
        {
            return held;
        }

        return null;
    }

    /// <summary><paramref name="handle"/> holds <paramref name="view"/>.</summary>
    public void Held(nint handle, HeldTextureView view)
    {
        Forget(handle);
        _byHandle[handle] = view;
        _byTexture.GetOrAdd(view.Texture, _ => new ConcurrentDictionary<nint, byte>())[handle] = 0;
        _any = true;
    }

    /// <summary>The descriptor at <paramref name="handle"/> is written anew: it no longer holds a view this class knows.</summary>
    [HotPath]
    public void Forget(nint handle)
    {
        if (!_any || !_byHandle.TryRemove(handle, out var held))
        {
            return;
        }

        if (_byTexture.TryGetValue(held.Texture, out var handles))
        {
            handles.TryRemove(handle, out _);
        }
    }

    /// <summary><paramref name="count"/> descriptors were copied from <paramref name="source"/> to <paramref name="destination"/>: the copies hold what the sources held.</summary>
    [HotPath]
    public void Copied(nint destination, nint source, uint count, uint increment)
    {
        if (!_any)
        {
            return;
        }

        for (uint i = 0; i < count; i++)
        {
            nint to = destination + (nint)((ulong)i * increment);
            nint from = source + (nint)((ulong)i * increment);

            if (_byHandle.TryGetValue(from, out var held))
            {
                Held(to, held);
            }
            else
            {
                Forget(to);
            }
        }
    }

    /// <summary>The handles holding a view of <paramref name="texture"/> now, with each view's dimension.</summary>
    public List<(nint Handle, int Dimension)> Of(GpuTexture texture)
    {
        return [.. Views(texture).Select(view => (view.Handle, view.View.Dimension))];
    }

    /// <summary>The handles holding a view of <paramref name="texture"/> now, with the views.</summary>
    public List<(nint Handle, HeldTextureView View)> Views(GpuTexture texture)
    {
        var found = new List<(nint Handle, HeldTextureView View)>();

        if (!_byTexture.TryGetValue(texture, out var handles))
        {
            return found;
        }

        foreach (var handle in handles.Keys)
        {
            if (_byHandle.TryGetValue(handle, out var held) && held.Texture == texture)
            {
                found.Add((handle, held));
            }
        }

        return found;
    }

    /// <summary>Whether <paramref name="handle"/> still holds a view of <paramref name="texture"/>.</summary>
    public bool Holds(nint handle, GpuTexture texture)
    {
        return _byHandle.TryGetValue(handle, out var held) && held.Texture == texture;
    }

    /// <summary>Forgets <paramref name="texture"/> and its handles (it is released).</summary>
    public void Drop(GpuTexture texture)
    {
        if (!_byTexture.TryRemove(texture, out var handles))
        {
            return;
        }

        foreach (var handle in handles.Keys)
        {
            if (_byHandle.TryGetValue(handle, out var held) && held.Texture == texture)
            {
                _byHandle.TryRemove(handle, out _);
            }
        }
    }
}
