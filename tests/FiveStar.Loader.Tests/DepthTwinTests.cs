// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Graphics;
using FiveStar.Graphics.Rendering;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Shims.D3D12X.Pipelines;
using FiveStar.Loader.Shims.D3D12X.Resources;
using FiveStar.Loader.Tests.Support;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// --depth-twins on WARP: a compute pass writes the colour twin of a depth texture through its UAV, and the copy pass
/// puts those values in the depth, where a draw's depth test sees them.
/// </summary>
public sealed unsafe class DepthTwinTests
{
    private const int Size = 8;
    private const float Written = 0.25f;

    /// <summary>D3D12_RESOURCE_STATE_DEPTH_WRITE.</summary>
    private const int DepthWrite = 0x10;

    /// <summary>The stencil the depth starts with and the mask the twin holds: every bit differs, so each bit pass shows.</summary>
    private const byte OldStencil = 0x5A;
    private const uint Mask = 0xA5;

    private const uint StencilPlane = 1;

    [WarpFact]
    public void A_value_written_to_the_twin_reaches_the_depth()
    {
        using var gpu = GpuDevice.CreateWarp(debugLayer: false);
        var device = gpu.Device;
        using var views = device.CreateDescriptorHeap(new Vortice.Direct3D12.DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, DescriptorHeapFlags.ShaderVisible));
        using var targets = device.CreateDescriptorHeap(new Vortice.Direct3D12.DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1));
        using var context = new CommandContext(device);
        using var arena = new ResourceArena(device);

        var description = Vortice.Direct3D12.ResourceDescription.Texture2D(Format.R32_Typeless, Size, Size, 1, 1, flags: ResourceFlags.AllowDepthStencil);
        using var depth = device.CreateCommittedResource(HeapType.Default, description, ResourceStates.DepthWrite, new ClearValue(Format.D32_Float, 1.0f, 0));
        device.CreateDepthStencilView(depth, new DepthStencilViewDescription { Format = Format.D32_Float, ViewDimension = DepthStencilViewDimension.Texture2D }, targets.GetCPUDescriptorHandleForHeapStart());

        var twin = DepthTwins.For(device.NativePointer, depth.NativePointer, Resource(description), DepthTwins.Float32);
        Assert.NotNull(twin);
        device.CreateUnorderedAccessView(Resource(twin.Twin), null, new UnorderedAccessViewDescription { Format = Format.R32_Float, ViewDimension = UnorderedAccessViewDimension.Texture2D }, views.GetCPUDescriptorHandleForHeapStart());

        var list = context.List;
        list.SetDescriptorHeaps(1, new[] { views });
        ulong table = views.GetGPUDescriptorHandleForHeapStart().Ptr;
        var view = new UavView((int)Format.R32_Float, UavViews.Texture2D, Size, Size, 1);

        Assert.True(UavFill.Record(device.NativePointer, list.NativePointer, table, 0, view, new FillValue(Written, Written, Written, 0)));
        Assert.True(DepthTwinCopy.Record(device.NativePointer, list.NativePointer, twin, (nint)targets.GetCPUDescriptorHandleForHeapStart().Ptr, (int)Format.D32_Float, DepthWrite, table));
        context.ExecuteAndWait();

        Assert.All(Read(context, arena, depth), value => Assert.Equal(Written, value));
    }

    [WarpFact]
    public void A_mask_written_to_the_stencil_twin_reaches_the_stencil_plane()
    {
        using var gpu = GpuDevice.CreateWarp(debugLayer: false);
        var device = gpu.Device;
        using var views = device.CreateDescriptorHeap(new Vortice.Direct3D12.DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, DescriptorHeapFlags.ShaderVisible));
        using var targets = device.CreateDescriptorHeap(new Vortice.Direct3D12.DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1));
        using var context = new CommandContext(device);
        using var arena = new ResourceArena(device);

        var description = Vortice.Direct3D12.ResourceDescription.Texture2D(Format.R32G8X24_Typeless, Size, Size, 1, 1, flags: ResourceFlags.AllowDepthStencil);
        using var depth = device.CreateCommittedResource(HeapType.Default, description, ResourceStates.DepthWrite, new ClearValue(Format.D32_Float_S8X24_UInt, 1.0f, OldStencil));
        var handle = targets.GetCPUDescriptorHandleForHeapStart();
        device.CreateDepthStencilView(depth, new DepthStencilViewDescription { Format = Format.D32_Float_S8X24_UInt, ViewDimension = DepthStencilViewDimension.Texture2D }, handle);

        // The game's stencil UAV is R8_UINT; the twin takes that format, and the stencil it held before is replaced.
        var twin = DepthTwins.For(device.NativePointer, depth.NativePointer, Resource(description), DepthTwins.Uint8);
        Assert.NotNull(twin);
        Assert.True(twin.Stencil);
        device.CreateUnorderedAccessView(Resource(twin.Twin), null, new UnorderedAccessViewDescription { Format = Format.R8_UInt, ViewDimension = UnorderedAccessViewDimension.Texture2D }, views.GetCPUDescriptorHandleForHeapStart());

        var list = context.List;
        list.ClearDepthStencilView(handle, ClearFlags.Depth | ClearFlags.Stencil, 1.0f, OldStencil);
        list.SetDescriptorHeaps(1, new[] { views });
        ulong table = views.GetGPUDescriptorHandleForHeapStart().Ptr;
        var view = new UavView((int)Format.R8_UInt, UavViews.Texture2D, Size, Size, 1);

        Assert.True(UavFill.Record(device.NativePointer, list.NativePointer, table, 0, view, new FillValue(0, 0, 0, Mask)));
        Assert.True(DepthTwinCopy.Record(device.NativePointer, list.NativePointer, twin, (nint)handle.Ptr, (int)Format.D32_Float_S8X24_UInt, DepthWrite, table));
        context.ExecuteAndWait();

        Assert.All(ReadPlane(device, context, arena, depth, StencilPlane), value => Assert.Equal(Mask, value));
        Assert.All(ReadPlane(device, context, arena, depth, 0), value => Assert.Equal(1.0f, BitConverter.UInt32BitsToSingle(value)));
    }

    [Fact]
    public void The_depth_and_stencil_plane_formats_take_a_twin()
    {
        Assert.True(DepthTwins.Twinned(DepthTwins.Float32));
        Assert.True(DepthTwins.Twinned(DepthTwins.Unorm16));
        Assert.True(DepthTwins.Twinned((int)Format.R8_UInt));
        Assert.True(DepthTwins.Twinned((int)Format.X32_Typeless_G8X24_UInt));
        Assert.False(DepthTwins.Twinned((int)Format.R32_UInt));

        Assert.Equal((int)Format.R8_UInt, DepthTwins.TwinFormat((int)Format.X32_Typeless_G8X24_UInt));
        Assert.Equal(DepthTwins.Float32, DepthTwins.TwinFormat(DepthTwins.Float32));
    }

    [Theory]
    [InlineData((int)Format.X32_Typeless_G8X24_UInt, 0, 1)]
    [InlineData((int)Format.R32_Float_X8X24_Typeless, 1, 0)]
    [InlineData((int)Format.X32_Typeless_G8X24_UInt, 1, 1)]
    public void A_plane_view_names_the_plane_its_format_reads(int format, int named, int expected)
    {
        const int PlaneDword = 6;

        // D3D12_SHADER_RESOURCE_VIEW_DESC, TEXTURE2D: format, dimension, mapping, padding, then mip, mip count, plane.
        int* description = stackalloc int[10] { format, (int)ShaderResourceViewDimension.Texture2D, 0x1688, 0, 0, 1, named, 0, 0, 0 };
        byte* copy = stackalloc byte[DepthViewDescriptions.ShaderResourceViewSize];

        var written = (int*)DepthViewDescriptions.ShaderResourceView(0, description, copy);

        Assert.Equal(expected, written[PlaneDword]);
        Assert.Equal(format, written[0]);
    }

    [Theory]
    [InlineData((int)Format.D32_Float_S8X24_UInt, (int)Format.R32_Float_X8X24_Typeless)]
    [InlineData((int)Format.D32_Float, (int)Format.R32_Float)]
    [InlineData((int)Format.D24_UNorm_S8_UInt, (int)Format.R24_UNorm_X8_Typeless)]
    [InlineData((int)Format.D16_UNorm, (int)Format.R16_UNorm)]
    public void A_depth_format_view_reads_the_depth_plane(int format, int expected)
    {
        const int PlaneDword = 6;

        int* description = stackalloc int[10] { format, (int)ShaderResourceViewDimension.Texture2D, 0x1688, 0, 0, 1, 1, 0, 0, 0 };
        byte* copy = stackalloc byte[DepthViewDescriptions.ShaderResourceViewSize];

        var written = (int*)DepthViewDescriptions.ShaderResourceView(0, description, copy);

        Assert.Equal(expected, written[0]);

        if (DepthFormats.Plane(expected) == DepthFormats.DepthPlane)
        {
            Assert.Equal(0, written[PlaneDword]);
        }
    }

    [Fact]
    public void A_colour_view_keeps_its_desc()
    {
        int* description = stackalloc int[10] { (int)Format.R8G8B8A8_UNorm, (int)ShaderResourceViewDimension.Texture2D, 0x1688, 0, 0, 1, 0, 0, 0, 0 };
        byte* copy = stackalloc byte[DepthViewDescriptions.ShaderResourceViewSize];

        Assert.True(DepthViewDescriptions.ShaderResourceView(0, description, copy) == description);
    }

    private static ID3D12Resource Resource(nint pointer)
    {
        System.Runtime.InteropServices.Marshal.AddRef(pointer);

        return new ID3D12Resource(pointer);
    }

    private static Shims.D3D12X.ResourceDescription Resource(Vortice.Direct3D12.ResourceDescription description)
    {
        return new Shims.D3D12X.ResourceDescription
        {
            Dimension = (int)description.Dimension, Width = description.Width, Height = description.Height,
            DepthOrArraySize = description.DepthOrArraySize, MipLevels = description.MipLevels, Format = (int)description.Format,
        };
    }

    /// <summary>The first row of plane <paramref name="plane"/> of a depth-stencil texture, one value per texel (stencil bytes widened).</summary>
    private static uint[] ReadPlane(ID3D12Device device, CommandContext context, ResourceArena arena, ID3D12Resource depth, uint plane)
    {
        var footprints = new PlacedSubresourceFootPrint[1];
        var rows = new uint[1];
        var rowBytes = new ulong[1];
        device.GetCopyableFootprints(depth.Description, plane, 1, 0, footprints, rows, rowBytes, out ulong total);

        var readback = arena.ReadbackBuffer((int)total);
        context.List.ResourceBarrierTransition(depth, ResourceStates.DepthWrite, ResourceStates.CopySource, plane);
        context.List.CopyTextureRegion(new TextureCopyLocation(readback, footprints[0]), 0, 0, 0, new TextureCopyLocation(depth, plane));
        context.List.ResourceBarrierTransition(depth, ResourceStates.CopySource, ResourceStates.DepthWrite, plane);
        context.ExecuteAndWait();

        void* mapped;
        readback.Map(0, null, &mapped).CheckError();
        var row = new uint[Size];

        for (var texel = 0; texel < Size; texel++)
        {
            row[texel] = Texel(mapped, plane, texel);
        }

        readback.Unmap(0);

        return row;
    }

    private static uint Texel(void* mapped, uint plane, int texel)
    {
        if (plane == StencilPlane)
        {
            return ((byte*)mapped)[texel];
        }

        return ((uint*)mapped)[texel];
    }

    /// <summary>The first row of the depth as floats.</summary>
    private static float[] Read(CommandContext context, ResourceArena arena, ID3D12Resource depth)
    {
        const int Pitch = 256;

        var readback = arena.ReadbackBuffer(Pitch * Size);
        var footprint = new PlacedSubresourceFootPrint { Footprint = new SubresourceFootPrint(Format.R32_Typeless, Size, Size, 1, Pitch) };
        context.List.ResourceBarrierTransition(depth, ResourceStates.DepthWrite, ResourceStates.CopySource);
        context.List.CopyTextureRegion(new TextureCopyLocation(readback, footprint), 0, 0, 0, new TextureCopyLocation(depth, 0));
        context.ExecuteAndWait();

        void* mapped;
        readback.Map(0, null, &mapped).CheckError();
        var row = new float[Size];
        new Span<float>(mapped, Size).CopyTo(row);
        readback.Unmap(0);

        return row;
    }
}
