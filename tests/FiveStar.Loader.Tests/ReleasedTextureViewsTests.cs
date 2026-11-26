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
/// Every boot lost the device minutes into the world: a descriptor kept naming a texture the registry released when
/// another took its address. On WARP: the views held of a released texture, copies included, are written over the
/// replacing texture of its shape or nulled, and the release waits for the work submitted after that.
/// </summary>
public sealed unsafe class ReleasedTextureViewsTests
{
    private const ulong Address = 0x4_0000_0000;
    private const ulong TextureBytes = 0x1000;
    private const int Size = 64;
    private const int Rgba8Unorm = 28;
    private const int Bc5Unorm = 83;
    private const int SrvTexture2D = 4;
    private const int ComponentMapping = 0x1688;
    private const int MipLevelsDword = 5;

    [WarpFact]
    public void A_released_textures_views_and_their_copies_go_to_its_replacement_or_become_null()
    {
        using var gpu = GpuDevice.CreateWarp(debugLayer: false);
        using var heap = gpu.Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2));
        nint device = gpu.Device.NativePointer;
        uint increment = gpu.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        nint first = (nint)heap.GetCPUDescriptorHandleForHeapStart().Ptr;

        var views = new TextureViewHandles();
        var counters = new GpuResourceCounters();
        var released = new ReleasedTextureViews(views, counters, null);
        var old = Texture(gpu.Device, Address, Format.R8G8B8A8_UNorm, Rgba8Unorm);
        var same = Texture(gpu.Device, Address, Format.R8G8B8A8_UNorm, Rgba8Unorm);
        var other = Texture(gpu.Device, Address, Format.BC5_UNorm, Bc5Unorm);

        // A view of the old texture, and a descriptor table copy of it.
        int* desc = stackalloc int[TextureViewDescription.Dwords];
        new Span<int>(desc, TextureViewDescription.Dwords).Clear();
        (desc[0], desc[1], desc[2], desc[MipLevelsDword]) = (Rgba8Unorm, SrvTexture2D, ComponentMapping, 1);
        ReleasedTextureViews.Create(device, false, old.Desktop, desc, true, first);
        views.Written(first, old, old.Desktop, desc, unordered: false);

        var start = heap.GetCPUDescriptorHandleForHeapStart();
        gpu.Device.CopyDescriptorsSimple(1, new CpuDescriptorHandle(start, 1, increment), start, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        views.Copied(first + (nint)increment, first, 1, increment);

        // Its replacement has its shape and format: both now view the replacement.
        Assert.Equal([old], released.Rewrite(device, [old], _ => same));
        Assert.Empty(views.Of(old));
        Assert.Equal(2, views.Of(same).Count);
        Assert.Equal(2, counters[GpuResourceEvent.ReleaseViewRepointed]);

        // The replacement goes in turn to a BC5 texture (the device-loss case): both become null views.
        Assert.Equal([same], released.Rewrite(device, [same], _ => other));
        Assert.Empty(views.Of(same));
        Assert.Equal(2, counters[GpuResourceEvent.ReleaseViewNulled]);
        Assert.Empty(released.Rewrite(device, [same], _ => null));

        foreach (var texture in new[] { old, same, other })
        {
            Marshal.Release(texture.Desktop);
        }
    }

    [WarpFact]
    public void A_texture_whose_views_were_rewritten_is_released_after_the_work_submitted_since()
    {
        using var gpu = GpuDevice.CreateWarp(debugLayer: false);
        ulong[] submitted = [1];
        ulong[] completed = [1];
        var registry = new GpuTextureRegistry(() => submitted, () => completed);
        var order = new List<string>();

        registry.Unviewing = textures =>
        {
            order.Add("rewrite");
            submitted = [5];

            return [.. textures];
        };
        registry.Releasing = _ => order.Add("release");

        // The newer texture takes part of the old one's memory: the old one is retired.
        registry.Register(Texture(gpu.Device, Address, Format.R8G8B8A8_UNorm, Rgba8Unorm));
        registry.Register(Texture(gpu.Device, Address + (TextureBytes / 2), Format.R8G8B8A8_UNorm, Rgba8Unorm));

        // Its grace and work are done: views rewritten, but a list submitted since may still read the old descriptor.
        registry.Advance(GpuTextureRegistry.GraceFrames);
        Assert.Equal(["rewrite"], order);
        Assert.Equal((0L, 1), (registry.Released, registry.RetiredCount));

        completed = [5];
        registry.Advance(GpuTextureRegistry.GraceFrames + 1);
        Assert.Equal(["rewrite", "release"], order);
        Assert.Equal(1, registry.Released);
        registry.Dispose();
    }

    private static GpuTexture Texture(ID3D12Device device, ulong address, Format format, int consoleFormat)
    {
        var resource = device.CreateCommittedResource(HeapType.Default, Vortice.Direct3D12.ResourceDescription.Texture2D(format, Size, Size, 1, 1), ResourceStates.Common);
        nint desktop = resource.NativePointer;
        Marshal.AddRef(desktop);
        resource.Dispose();
        var description = new Shims.D3D12X.ResourceDescription { Dimension = 3, Width = Size, Height = Size, DepthOrArraySize = 1, MipLevels = 1, Format = consoleFormat, SampleCount = 1 };

        return new GpuTexture(address, description, desktop, consoleFormat, 0, false, new XgSurface { Size = TextureBytes });
    }
}
