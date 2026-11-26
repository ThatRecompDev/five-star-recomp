// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Numerics;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// Before the registry releases a texture (<see cref="GpuTextureRegistry.Unviewing"/>), every descriptor still holding a
/// view of it, copies included (<see cref="TextureViewHandles"/>), is written again: over the texture that replaced it at
/// its address when that one has its shape, format and flags (the console's descriptor reads that memory), else as a null
/// view of the same dimension. A descriptor left naming the released texture read freed memory, and the device was
/// removed (a BC7 sRGB view at heap index 16903 over the BC5 texture that took its address). The registry then waits for
/// the queues' work submitted after the rewrite before it releases.
/// </summary>
internal sealed unsafe class ReleasedTextureViews
{
    /// <summary>ID3D12Device::CreateShaderResourceView and CreateUnorderedAccessView (d3d12.h).</summary>
    private const int CreateShaderResourceViewSlot = 18;
    private const int CreateUnorderedAccessViewSlot = 19;

    /// <summary>D3D12_UNORDERED_ACCESS_VIEW_DESC: R8G8B8A8_UNORM, its dimension, and the union from dword 2.</summary>
    private const int NullFormat = 28;
    private const int FormatDword = 0;
    private const int DimensionDword = 1;
    private const int ArraySizeDword = 4;

    /// <summary>D3D12_UAV_DIMENSION values (d3d12.h).</summary>
    private const int UavTexture1D = 2;
    private const int UavTexture1DArray = 3;
    private const int UavTexture2D = 4;
    private const int UavTexture2DArray = 5;
    private const int UavTexture3D = 8;

    private readonly TextureViewHandles _views;
    private readonly GpuResourceCounters _counters;
    private readonly LoaderLog? _log;
    private readonly ConcurrentDictionary<string, byte> _noted = new();
    private long _rewritten;

    public ReleasedTextureViews(TextureViewHandles views, GpuResourceCounters counters, LoaderLog? log)
    {
        _views = views;
        _counters = counters;
        _log = log;
    }

    /// <summary>
    /// Writes again the views of <paramref name="textures"/>, about to be released, on <paramref name="device"/>;
    /// <paramref name="find"/> gives the texture registered at an address now. Returns the textures whose views it wrote.
    /// </summary>
    public List<GpuTexture> Rewrite(nint device, IReadOnlyList<GpuTexture> textures, Func<ulong, GpuTexture?> find)
    {
        var rewritten = new List<GpuTexture>();

        if (device == 0)
        {
            return rewritten;
        }

        foreach (var texture in textures)
        {
            var held = _views.Views(texture);

            if (held.Count == 0)
            {
                continue;
            }

            var replacement = Replacing(texture, find(texture.Address));

            foreach (var (handle, view) in held)
            {
                Rewrite(device, handle, view, replacement);
            }

            rewritten.Add(texture);
        }

        if (DebugLayer.ViewLiveness.Noting && textures.Count > 0)
        {
            CheckUnaccounted(textures);
        }

        Report(rewritten.Count);

        return rewritten;
    }

    /// <summary>
    /// <paramref name="texture"/> is released now: the shared-exponent shadow and depth twins made of it no longer copy
    /// from it (each kind named once).
    /// </summary>
    public void Released(GpuTexture texture)
    {
        if (SharedExponentAliases.Released(texture))
        {
            Unaccounted(GpuResourceEvent.ReleaseDroppedShadow, "shadow", "a released R32_UINT texture's R9G9B9E5 shadow is no longer copied into; views of the shadow stay (it is kept)");
        }

        if (DepthTwins.Released(texture.Desktop))
        {
            Unaccounted(GpuResourceEvent.ReleaseDroppedTwin, "twin", "a released depth texture's twins are forgotten; their UAVs stay (the twins are kept)");
        }
    }

    /// <summary>The texture registered at the released one's address when it can take the released one's views; else null.</summary>
    internal static GpuTexture? Replacing(GpuTexture released, GpuTexture? candidate)
    {
        if (candidate is null || candidate == released || !candidate.Matches(released.Address, released.Console))
        {
            return null;
        }

        var (was, now) = (released.Console, candidate.Console);

        if (candidate.DesktopFormat != released.DesktopFormat || candidate.GpuWritten != released.GpuWritten || now.Flags != was.Flags || now.SampleCount != was.SampleCount)
        {
            return null;
        }

        return candidate;
    }

    private void Rewrite(nint device, nint handle, HeldTextureView view, GpuTexture? replacement)
    {
        // The game wrote the descriptor anew meanwhile: it is not ours to write.
        if (!_views.Holds(handle, view.Texture))
        {
            return;
        }

        if (replacement is not null && view.Target == view.Texture.Desktop)
        {
            var description = view.Description;
            Create(device, view.Unordered, replacement.Desktop, &description, view.Described, handle);
            _views.Held(handle, new HeldTextureView(replacement, view.Dimension, view.Unordered, replacement.Desktop, view.Described, description));
            NoteLiveness(handle, replacement.Desktop, view.Unordered);
            _counters.Add(GpuResourceEvent.ReleaseViewRepointed);

            return;
        }

        var none = default(TextureViewDescription);
        NullDescription((int*)&none, view.Unordered, view.Dimension);
        Create(device, view.Unordered, 0, &none, true, handle);
        _views.Forget(handle);
        NoteLiveness(handle, 0, view.Unordered);
        _counters.Add(GpuResourceEvent.ReleaseViewNulled);
    }

    /// <summary>Writes an SRV or UAV of <paramref name="resource"/> (0: a null view) at <paramref name="handle"/>, with the desc when <paramref name="described"/>.</summary>
    internal static void Create(nint device, bool unordered, nint resource, void* description, bool described, nint handle)
    {
        void* desc = described ? description : null;

        if (unordered)
        {
            ((delegate* unmanaged<nint, nint, nint, void*, nint, void>)ComObject.Slot(device, CreateUnorderedAccessViewSlot))(device, resource, 0, desc, handle);

            return;
        }

        ((delegate* unmanaged<nint, nint, void*, nint, void>)ComObject.Slot(device, CreateShaderResourceViewSlot))(device, resource, desc, handle);
    }

    /// <summary>A null SRV (<see cref="GpuTextures.NullViewDescription"/>) or UAV desc of <paramref name="dimension"/>.</summary>
    internal static void NullDescription(int* desc, bool unordered, int dimension)
    {
        if (!unordered)
        {
            GpuTextures.NullViewDescription(desc, dimension);

            return;
        }

        new Span<int>(desc, TextureViewDescription.Dwords).Clear();
        desc[FormatDword] = NullFormat;
        desc[DimensionDword] = UavTexture2D;

        if (dimension is UavTexture1D or UavTexture1DArray or UavTexture2D or UavTexture2DArray or UavTexture3D)
        {
            desc[DimensionDword] = dimension;
        }

        // Texture1DArray and Texture2DArray: MipSlice, FirstArraySlice, ArraySize; Texture3D: MipSlice, FirstWSlice, WSize.
        if (desc[DimensionDword] is UavTexture1DArray or UavTexture2DArray or UavTexture3D)
        {
            desc[ArraySizeDword] = 1;
        }
    }

    private static void NoteLiveness(nint handle, nint resource, bool unordered)
    {
        if (DebugLayer.ViewLiveness.Noting)
        {
            var shape = DebugLayer.ViewShape.Null;

            if (resource != 0)
            {
                shape = unordered ? DebugLayer.ViewShape.TextureUav : DebugLayer.ViewShape.TextureSrv;
            }

            DebugLayer.ViewLiveness.Written(handle, resource, shape);
        }
    }

    /// <summary>With view liveness noted: views of the released textures this class did not know of, counted and named once per shape.</summary>
    private void CheckUnaccounted(IReadOnlyList<GpuTexture> textures)
    {
        var resources = textures.Select(texture => texture.Desktop).ToHashSet();

        foreach (var (handle, view) in DebugLayer.ViewLiveness.Naming(resources))
        {
            Unaccounted(GpuResourceEvent.ReleaseViewUnaccounted, $"view-{view.Shape}", $"a {view.Shape} view at handle 0x{handle:x} still names a released texture (0x{view.Resource:x}), written by a path that does not note texture views; left as it is");
        }
    }

    private void Unaccounted(GpuResourceEvent kind, string key, string what)
    {
        _counters.Add(kind);

        if (_noted.TryAdd(key, 0))
        {
            _log?.Warn($"[d3d12_xs] texture release: {what} (counted as {kind}; named once)");
        }
    }

    /// <summary>The first release that wrote views, and then at powers of two, logs the counts.</summary>
    private void Report(int rewritten)
    {
        if (rewritten == 0)
        {
            return;
        }

        long total = Interlocked.Add(ref _rewritten, rewritten);

        // Logged when the count passes a power of two.
        if (total == rewritten || BitOperations.Log2((ulong)total) != BitOperations.Log2((ulong)(total - rewritten)))
        {
            _log?.Info($"[d3d12_xs] texture release: {total} released textures had views held, {_counters[GpuResourceEvent.ReleaseViewNulled]} nulled, {_counters[GpuResourceEvent.ReleaseViewRepointed]} written over the texture replacing them, {_counters[GpuResourceEvent.ReleaseViewUnaccounted]} unaccounted; each released after the queues' work submitted since");
        }
    }
}
