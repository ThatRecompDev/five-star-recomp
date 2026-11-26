// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Shims.D3D12X;
using FiveStar.Loader.Shims.D3D12X.Resources;
using FiveStar.Loader.Shims.Xg;
using FiveStar.Loader.Tests.Support;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>Streamed textures refreshed page by page: only the subresources a changed page lies in are uploaded.</summary>
public sealed unsafe class TextureRefreshTests
{
    private const int Size = 256;
    private const int Mips = 2;
    private const int Rgba8Unorm = 28;
    private const int Bc7Unorm = 98;
    private const int Texture2D = 3;
    private const int SrvTexture2D = 4;
    private const int DefaultComponentMapping = 0x1688;
    private const int TextureMipLevelsWord = 5;
    private const int ShaderResourceLayout = 6;
    private const int CloseSlot = 9;
    private const ulong PageSize = 0x10000;
    private const int RangePages = 8;
    private const uint Reserve64K = 0x20402000;
    private const uint CpuReadWrite = 0x4;
    private const uint GpuOnly = 0x801;

    /// <summary>Every element a copy of a subresource reads lies in the span the refresh reads for it.</summary>
    [Theory]
    [InlineData(Rgba8Unorm, 256, 256, 1, 9)]
    [InlineData(Bc7Unorm, 512, 512, 4, 10)]
    [InlineData(Bc7Unorm, 1024, 256, 1, 11)]
    public void A_subresource_span_holds_every_element_of_the_subresource(int format, int width, int height, int slices, int mips)
    {
        var desc = Desc(format, width, height, slices, mips);
        XgLayoutBuilder.Build(desc, out var surface);
        var plane = surface.Planes[0];

        for (int subresource = 0; subresource < plane.MipCount * plane.Slices; subresource++)
        {
            var (start, end) = XgSubresourceSpans.Of(surface, subresource);
            int mip = subresource % plane.MipCount;
            var level = plane.Mips[mip];

            for (uint y = 0; y < level.HeightElements; y++)
            {
                for (uint x = 0; x < level.WidthElements; x++)
                {
                    ulong at = XgAddress.Element(plane, mip, subresource / plane.MipCount, x, y);
                    Assert.InRange(at, start, end - (ulong)plane.BytesPerElement);
                }
            }
        }
    }

    [Fact]
    public void Only_the_subresources_a_changed_page_lies_in_are_affected()
    {
        XgLayoutBuilder.Build(Desc(Rgba8Unorm, Size, Size, 1, Mips), out var surface);
        int pages = (int)((surface.Size + PageSize - 1) / PageSize);
        var (start, end) = XgSubresourceSpans.Of(surface, 1);
        var changed = new bool[pages];
        changed[(int)(start / PageSize)] = true;
        var needed = new bool[pages];

        var affected = TextureRefreshWorker.Affected(surface, Mips, changed, needed);

        Assert.Equal([false, true], affected);
        // The pages mip 1 spans are read whole; mip 0's other pages are not.
        var spanned = Enumerable.Range(0, pages).Select(page => (ulong)page >= start / PageSize && (ulong)page <= (end - 1) / PageSize);
        Assert.Equal(spanned, needed);
        Assert.Contains(false, needed);
    }

    /// <summary>
    /// A DirectStorage write of mip 1 only: the refresh uploads mip 1 (UploadPartial), mip 0 keeps its texels, and a
    /// write of the same bytes again uploads nothing.
    /// </summary>
    [Fact]
    public void A_write_of_one_mip_uploads_that_mip_only()
    {
        using var test = TestRuntime.Create();
        var (gpu, frames) = Reserved(test);
        var device = new XboxDevice(test);
        var description = Description();
        var textures = test.Runtime.D3D12X.Textures;
        XgLayoutBuilder.Build(Desc(Rgba8Unorm, Size, Size, 1, Mips, description.Layout), out var surface);

        byte[][] first = [Pattern(Size, 1), Pattern(Size / 2, 2)];
        Write(test, surface, frames, first, 0, surface.Size);
        PlaceTexture(device, gpu, description);
        using var probe = new TextureProbe(test.Runtime.D3D12X.Objects.DesktopDevice);
        CreateView(device, gpu, description, (nint)probe.Heap.GetCPUDescriptorHandleForHeapStart().Ptr);
        long uploads = textures.Counters[GpuResourceEvent.Upload];

        // Mip 1's pages only, mip 0's bytes in them unchanged.
        var (start, end) = XgSubresourceSpans.Of(surface, 1);
        byte[][] second = [first[0], Pattern(Size / 2, 4)];
        Write(test, surface, frames, second, start / PageSize * PageSize, end);
        RunEmptyList(device);

        Assert.Equal(1, textures.Counters[GpuResourceEvent.UploadPartial]);
        Assert.Equal(uploads, textures.Counters[GpuResourceEvent.Upload]);
        Assert.Equal(second[1], Bytes(probe.Read(Size / 2, Size / 2, 1, textures.Uploader.OrderBefore)));
        Assert.Equal(first[0], Bytes(probe.Read(Size, Size, 0, textures.Uploader.OrderBefore)));

        long skipped = textures.Counters[GpuResourceEvent.UploadSkipped];
        Write(test, surface, frames, second, start / PageSize * PageSize, end);
        RunEmptyList(device);
        Assert.Equal(1, textures.Counters[GpuResourceEvent.UploadPartial]);
        Assert.True(textures.Counters[GpuResourceEvent.UploadSkipped] > skipped);
    }

    /// <summary>
    /// Views of a filled texture: the first of a frame queues the check of its pages on the worker, the others in that
    /// frame cost nothing; bytes the CPU wrote into reserved memory (which nothing reports) are found by the next
    /// frame's view and uploaded before the submission after it.
    /// </summary>
    [Fact]
    public void Views_check_a_texture_once_a_frame_and_find_what_the_cpu_wrote()
    {
        using var test = TestRuntime.Create();
        var (gpu, frames) = Reserved(test);
        var device = new XboxDevice(test);
        var description = Description();
        var textures = test.Runtime.D3D12X.Textures;
        nint device12 = test.Runtime.D3D12X.Objects.DesktopDevice;

        XgLayoutBuilder.Build(Desc(Rgba8Unorm, Size, Size, 1, Mips, description.Layout), out var surface);
        byte[][] first = [Pattern(Size, 5), Pattern(Size / 2, 6)];
        Write(test, surface, frames, first, 0, surface.Size);
        PlaceTexture(device, gpu, description);

        using var probe = new TextureProbe(device12);
        nint handle = (nint)probe.Heap.GetCPUDescriptorHandleForHeapStart().Ptr;
        textures.RefreshWatched(device12, 1);

        for (int view = 0; view < 3; view++)
        {
            CreateView(device, gpu, description, handle);
        }

        Assert.Equal(1, textures.Counters[GpuResourceEvent.ViewCheckQueued]);
        Assert.Equal(2, textures.Counters[GpuResourceEvent.ViewCheckSkipped]);
        RunEmptyList(device);

        // The CPU writes new mips into the mapped range (no DirectStorage, no mapping): the next frame's view finds them.
        byte[][] second = [Pattern(Size, 7), Pattern(Size / 2, 8)];
        WriteCpu(test, surface, gpu, second);
        textures.RefreshWatched(device12, 2);
        CreateView(device, gpu, description, handle);
        RunEmptyList(device);

        Assert.Equal(second[0], Bytes(probe.Read(Size, Size, 0, textures.Uploader.OrderBefore)));
        Assert.Equal(second[1], Bytes(probe.Read(Size / 2, Size / 2, 1, textures.Uploader.OrderBefore)));
    }

    /// <summary>Tiles <paramref name="mips"/> straight into the CPU range behind GPU address <paramref name="gpu"/>, as the game's CPU would.</summary>
    private static void WriteCpu(TestRuntime test, in XgSurface surface, nint gpu, byte[][] mips)
    {
        var cpu = (byte*)test.Runtime.XMemPhysical.CpuAddress(gpu);

        for (int mip = 0; mip < mips.Length; mip++)
        {
            uint pitch = (uint)(Size >> mip) * sizeof(uint);

            fixed (byte* linear = mips[mip])
            {
                Assert.Equal(0, XgCopier.Into(surface, cpu, 0, (uint)mip, linear, pitch, pitch * (uint)(Size >> mip)));
            }
        }
    }

    private static byte[] Bytes(uint[] texels)
    {
        return texels.SelectMany(BitConverter.GetBytes).ToArray();
    }

    private static XgResourceDesc Desc(int format, int width, int height, int slices, int mips, int layout = 0)
    {
        if (layout == 0)
        {
            layout = (int)(XgSwizzleModes.ModeBase | XgSwizzleModes.Suggest((long)width * height * sizeof(uint)));
        }

        return new XgResourceDesc
        {
            Dimension = Texture2D, Width = (ulong)width, Height = (uint)height, DepthOrArraySize = (ushort)slices,
            MipLevels = (ushort)mips, Format = (uint)format, SampleCount = 1, Layout = (uint)layout,
        };
    }

    private static ResourceDescription Description()
    {
        var description = new ResourceDescription
        {
            Dimension = Texture2D, Width = Size, Height = Size, DepthOrArraySize = 1, MipLevels = Mips,
            Format = Rgba8Unorm, SampleCount = 1,
        };
        description.Layout = (int)(XgSwizzleModes.ModeBase | XgSwizzleModes.Suggest(Size * Size * sizeof(uint)));

        return description;
    }

    /// <summary>A CPU range with its GPU-only alias and pages mapped into the CPU range; the frames.</summary>
    private static (nint Gpu, ulong[] Frames) Reserved(TestRuntime test)
    {
        var virtualAlloc = (delegate* unmanaged<nint, nuint, uint, ulong, uint, nint>)test.Shim("xmem.dll", "XMemVirtualAlloc");
        var allocate = (delegate* unmanaged<ulong, ulong*, ulong*, int>)test.Shim("xmem.dll", "XMemAllocatePhysicalPages");
        var map = (delegate* unmanaged<nint, ulong, ulong*, nint>)test.Shim("xmem.dll", "XMemMapPhysicalPages");

        nint cpu = virtualAlloc(0, (nuint)(RangePages * PageSize), Reserve64K, 0x21, CpuReadWrite);
        nint gpu = virtualAlloc(0, (nuint)(RangePages * PageSize), Reserve64K, 0x3, GpuOnly);
        var frames = new ulong[RangePages];
        ulong count = RangePages;

        fixed (ulong* pages = frames)
        {
            Assert.Equal(1, allocate(0, &count, pages));
            Assert.Equal(cpu, map(cpu, RangePages, pages));
        }

        return (gpu, frames);
    }

    /// <summary>Tiles <paramref name="mips"/> and writes bytes [<paramref name="from"/>, <paramref name="to"/>) of them into the frames, as a page-list request.</summary>
    private static void Write(TestRuntime test, in XgSurface surface, ulong[] frames, byte[][] mips, ulong from, ulong to)
    {
        Assert.True(surface.Size <= RangePages * PageSize);
        var tiled = (byte*)NativeMemory.AllocZeroed((nuint)(RangePages * PageSize));

        try
        {
            for (int mip = 0; mip < mips.Length; mip++)
            {
                uint pitch = (uint)(Size >> mip) * sizeof(uint);

                fixed (byte* linear = mips[mip])
                {
                    Assert.Equal(0, XgCopier.Into(surface, tiled, 0, (uint)mip, linear, pitch, pitch * (uint)(Size >> mip)));
                }
            }

            fixed (ulong* pages = frames)
            {
                Assert.True(test.Runtime.XMemPhysical.WriteFrames(pages, from, new ReadOnlySpan<byte>(tiled + from, (int)(to - from))));
            }
        }
        finally
        {
            NativeMemory.Free(tiled);
        }
    }

    private static void PlaceTexture(XboxDevice device, nint address, ResourceDescription description)
    {
        var iid = new Guid("696442be-a72e-4059-bc79-5b5c98040fad");
        nint resource = 0;
        var create = (delegate* unmanaged<nint, nint, ResourceDescription*, uint, void*, Guid*, nint*, int>)XboxDevice.Vtable(device.Device)[133];
        Assert.Equal(0, create(device.Device, address, &description, ShaderResourceLayout, null, &iid, &resource));
    }

    private static void CreateView(XboxDevice device, nint address, ResourceDescription description, nint handle)
    {
        var view = new AddressViewDescription { Format = Rgba8Unorm, ViewDimension = SrvTexture2D, Address = (ulong)address };
        var words = (int*)&view;
        words[2] = DefaultComponentMapping;
        words[TextureMipLevelsWord] = Mips;
        var create = (delegate* unmanaged<nint, ResourceDescription*, AddressViewDescription*, nint, void>)XboxDevice.Vtable(device.Device)[70];
        create(device.Device, &description, &view, handle);
    }

    private static void RunEmptyList(XboxDevice device)
    {
        nint list = device.DirectList();
        Assert.Equal(0, ((delegate* unmanaged<nint, int>)XboxDevice.Vtable(list)[CloseSlot])(list));
        device.Run(list);
    }

    private static byte[] Pattern(int extent, int seed)
    {
        var bytes = new byte[extent * extent * sizeof(uint)];

        for (int index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)((index * 7) + (index / 251) + (seed * 31));
        }

        return bytes;
    }
}
