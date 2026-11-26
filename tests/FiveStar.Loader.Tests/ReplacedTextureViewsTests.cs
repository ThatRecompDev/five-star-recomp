// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Graphics;
using FiveStar.Loader.Shims.D3D12X.Resources;
using FiveStar.Loader.Shims.Xg;
using FiveStar.Loader.Tests.Support;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// Boot w120c: format-53 shadow pages re-made at another size over the same address while the game kept drawing into
/// the old one through its depth views. On WARP: a view of the old extent finds the old texture, which stays alive while
/// its depth is bound, and is released once it no longer is.
/// </summary>
public sealed class ReplacedTextureViewsTests
{
    private const ulong Address = 0x4_0000_0000;
    private const int R16Typeless = 53;

    [WarpFact]
    public void A_view_of_the_old_extent_reads_the_replaced_depth_while_it_is_drawn_into()
    {
        using var gpu = GpuDevice.CreateWarp(debugLayer: false);
        var registry = new GpuTextureRegistry(() => [], () => []);
        var old = Depth(gpu.Device, 1024);
        var newer = Depth(gpu.Device, 512);

        registry.Register(old);
        registry.Register(newer);

        Assert.Same(newer, registry.Find(Address));
        Assert.Same(old, registry.FindReplaced(Address, Description(1024)));
        Assert.Null(registry.FindReplaced(Address, Description(256)));

        // Bound past its grace: kept; then no longer bound: released.
        registry.Advance(GpuTextureRegistry.GraceFrames - 1);
        registry.Used(old.Desktop);
        registry.Advance(GpuTextureRegistry.GraceFrames + 1);
        Assert.Equal((0L, 1L), (registry.Released, registry.KeptInUse));

        registry.Advance((GpuTextureRegistry.GraceFrames * 3) + 1);
        Assert.Equal(1, registry.Released);
        registry.Dispose();
    }

    private static GpuTexture Depth(ID3D12Device device, int size)
    {
        var resource = device.CreateCommittedResource(HeapType.Default, Vortice.Direct3D12.ResourceDescription.Texture2D(Format.R16_Typeless, (uint)size, (uint)size, 1, 1, flags: ResourceFlags.AllowDepthStencil), ResourceStates.DepthWrite);
        nint desktop = resource.NativePointer;
        Marshal.AddRef(desktop);
        resource.Dispose();

        return new GpuTexture(Address, Description(size), desktop, R16Typeless, (int)ResourceStates.DepthWrite, true, new XgSurface { Size = (ulong)size * (ulong)size * 2 });
    }

    private static Shims.D3D12X.ResourceDescription Description(int size)
    {
        return new Shims.D3D12X.ResourceDescription { Dimension = 3, Width = (ulong)size, Height = (uint)size, DepthOrArraySize = 1, MipLevels = 1, Format = R16Typeless };
    }
}
