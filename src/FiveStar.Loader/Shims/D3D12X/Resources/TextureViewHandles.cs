// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// The desktop descriptor handles that hold a shader-resource view of a registered texture: noted where a view is
/// written (slot 70, CreateShaderResourceView) and where descriptors are copied, forgotten where a handle is written
/// anew (<see cref="XboxDescriptorHeaps.Destination(nint, Binding.BufferRecord)"/>). A texture over unmapped memory is
/// retired only while no window record names one of its handles' heap indices, and its views still there are nulled
/// first (<see cref="GpuTextures"/>), so a later read finds a null view, not a released texture.
/// </summary>
internal sealed class TextureViewHandles
{
    private readonly ConcurrentDictionary<nint, (GpuTexture Texture, int Dimension)> _byHandle = new();
    private readonly ConcurrentDictionary<GpuTexture, ConcurrentDictionary<nint, byte>> _byTexture = new();
    private volatile bool _any;

    /// <summary>A view of <paramref name="texture"/> of SRV dimension <paramref name="dimension"/> was written at <paramref name="handle"/>.</summary>
    public void Written(nint handle, GpuTexture texture, int dimension)
    {
        Forget(handle);
        _byHandle[handle] = (texture, dimension);
        _byTexture.GetOrAdd(texture, _ => new ConcurrentDictionary<nint, byte>())[handle] = 0;
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
                Written(to, held.Texture, held.Dimension);
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
        var found = new List<(nint Handle, int Dimension)>();

        if (!_byTexture.TryGetValue(texture, out var handles))
        {
            return found;
        }

        foreach (var handle in handles.Keys)
        {
            if (_byHandle.TryGetValue(handle, out var held) && held.Texture == texture)
            {
                found.Add((handle, held.Dimension));
            }
        }

        return found;
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
