// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Pipelines;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// Device slot 70: an SRV described by a resource desc and a console GPU address. The address resolves to the
/// registered texture there (refreshed from memory; a GPU-written one of the same extent is taken whatever its format),
/// else to a texture made from the desc over readable memory; with neither, or for a depth texture, the descriptor is
/// a null view of the same dimension.
/// </summary>
internal sealed unsafe class AddressShaderViews
{
    /// <summary>ID3D12Device::CreateShaderResourceView (d3d12.h).</summary>
    private const int DesktopShaderResourceViewSlot = 18;

    /// <summary>Views between two log lines with the counters (after the powers of two up to it).</summary>
    private const int SummaryInterval = 4096;

    private readonly GpuTextures _textures;
    private readonly AddressBufferViews _buffers;
    private readonly OnceLog _warnings;
    private readonly LoaderLog _log;

    public AddressShaderViews(GpuTextures textures, AddressBufferViews buffers, OnceLog warnings, LoaderLog log)
    {
        _textures = textures;
        _buffers = buffers;
        _warnings = warnings;
        _log = log;
    }

    /// <summary>Writes the view into <paramref name="handle"/>, a desktop CPU descriptor handle; returns its texture, null for a null view.</summary>
    public GpuTexture? Create(nint device, ResourceDescription* resource, AddressViewDescription* view, nint handle, Binding.BufferRecord record)
    {
        var counters = _textures.Counters;

        long views = counters.Add(GpuResourceEvent.View);

        if (BitOperations.IsPow2(views) || views % SummaryInterval == 0)
        {
            _log.Detail($"  [d3d12_xs] console-address textures: {counters}; {_textures.Registry.Summary}; {_textures.Placed.Summary}");
        }

        if (IsBuffer(resource) && view->Address != 0)
        {
            counters.Add(GpuResourceEvent.ViewBuffer);

            if (_buffers.Create(device, view, handle, record))
            {
                return null;
            }

            _warnings.Warn("slot70-buffer", $"[d3d12_xs] ID3D12Device.X::Slot70: a buffer view at 0x{view->Address:x} outside known game memory or its buffer: null descriptor");
        }

        var texture = Resolve(device, resource, view);
        var desktop = *view;
        int viewFormat = DepthFormats.ShaderView(view->Format);
        nint target = 0;

        if (texture is not null)
        {
            desktop.Format = ViewFormats.ForView(texture.DesktopFormat, viewFormat);
            desktop.ComponentMapping = TargetFormats.ComponentMapping(viewFormat, desktop.ComponentMapping);
            target = texture.Desktop;
        }

        if (target == 0 || desktop.Format == ViewFormats.Unknown)
        {
            counters.Add(GpuResourceEvent.ViewNull);
            _warnings.Warn("slot70-null", $"[d3d12_xs] ID3D12Device.X::Slot70: no readable texture at 0x{view->Address:x} (format {view->Format}): null descriptor");
            desktop.Format = viewFormat;
            target = 0;
            texture = null;
        }

        var create = (delegate* unmanaged<nint, nint, AddressViewDescription*, nint, void>)ComObject.Slot(device, DesktopShaderResourceViewSlot);
        create(device, target, &desktop, handle);

        return texture;
    }

    /// <summary>A buffer desc, or a texture desc without a format (raw or structured views): not a texture to make.</summary>
    private static bool IsBuffer(ResourceDescription* resource)
    {
        return resource != null && (resource->Dimension == ResourceDescription.BufferDimension || resource->Format == ViewFormats.Unknown);
    }

    private GpuTexture? Resolve(nint device, ResourceDescription* resource, AddressViewDescription* view)
    {
        ulong address = view->Address;

        if (resource == null || address == 0)
        {
            return null;
        }

        if (IsBuffer(resource))
        {
            return null;
        }

        var texture = _textures.Registry.Find(address);

        if (texture is not null && (texture.Matches(address, *resource) || (texture.GpuWritten && texture.SameExtent(address, *resource))))
        {
            _textures.Counters.Add(GpuResourceEvent.ViewResolved);
            _textures.Refresh(device, texture);

            return texture;
        }

        // A view inside a texture the GPU writes is not memory to read: making a texture there would retire it.
        if (texture is not null && texture.GpuWritten)
        {
            return null;
        }

        texture =_textures.CreateForView(device, address, resource, view->Format);

        if (texture is not null)
        {
            _textures.Counters.Add(GpuResourceEvent.ViewCreatedTexture);
        }

        return texture;
    }
}
