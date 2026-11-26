// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Graphics;
using FiveStar.Graphics.Rendering;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Shims.D3D12X.Resources;
using FiveStar.Loader.Tests.Support;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// sRGB targets with UAVs (the G-buffer albedo, R8G8B8A8_UNORM_SRGB, which CS_2GBufFixupAndClearTemp writes through a
/// UAV): made typeless, their UAVs UNORM (the bits stored as given, as on the console), their SRVs and RTVs sRGB, also
/// views made without a desc.
/// </summary>
public sealed unsafe class SrgbTargetTests
{
    private const int Size = 8;
    private const int AllowUnorderedAccess = 0x4;
    private const float Half = 0.5f;

    /// <summary>0.5 stored UNORM in each channel: 128 (0.5 * 255 rounds to even, 127.5 -> 128).</summary>
    private const uint HalfBits = 0x80808080;

    private const int Pitch = 256;

    [Theory]
    [InlineData((int)Format.R8G8B8A8_UNorm_SRgb, AllowUnorderedAccess, (int)Format.R8G8B8A8_Typeless)]
    [InlineData((int)Format.B8G8R8A8_UNorm_SRgb, AllowUnorderedAccess, (int)Format.B8G8R8A8_Typeless)]
    [InlineData((int)Format.B8G8R8X8_UNorm_SRgb, AllowUnorderedAccess, (int)Format.B8G8R8X8_Typeless)]
    [InlineData((int)Format.R8G8B8A8_UNorm_SRgb, 0, (int)Format.R8G8B8A8_UNorm_SRgb)]
    [InlineData((int)Format.R8G8B8A8_UNorm, AllowUnorderedAccess, (int)Format.R8G8B8A8_UNorm)]
    public void An_srgb_texture_with_uavs_is_made_typeless(int format, int flags, int expected)
    {
        Assert.Equal(expected, TypelessTargets.SrgbResource(format, flags));
    }

    [WarpFact]
    public void A_uav_writes_the_raw_bits_and_the_views_keep_srgb()
    {
        using var gpu = GpuDevice.CreateWarp(debugLayer: false);
        var device = gpu.Device;
        using var heap = device.CreateDescriptorHeap(new Vortice.Direct3D12.DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2, DescriptorHeapFlags.ShaderVisible));
        using var context = new CommandContext(device);
        using var arena = new ResourceArena(device);

        int made = TypelessTargets.SrgbResource((int)Format.R8G8B8A8_UNorm_SRgb, AllowUnorderedAccess);
        var description = Vortice.Direct3D12.ResourceDescription.Texture2D((Format)made, Size, Size, 1, 1, flags: ResourceFlags.AllowRenderTarget | ResourceFlags.AllowUnorderedAccess);
        using var albedo = device.CreateCommittedResource(HeapType.Default, description, ResourceStates.UnorderedAccess);
        TypelessTargets.NoteMade(albedo.NativePointer, (int)Format.R8G8B8A8_UNorm_SRgb, made);

        // The game's UAV is sRGB; the desktop takes it as UNORM. A view without a desc: UNORM for a UAV, sRGB otherwise.
        int explicitUav = ViewFormat(albedo, unordered: true, (int)Format.R8G8B8A8_UNorm_SRgb);
        int wholeUav = ViewFormat(albedo, unordered: true, null);
        int wholeSrv = ShaderViewFormat(albedo);

        Assert.Equal((int)Format.R8G8B8A8_UNorm, explicitUav);
        Assert.Equal((int)Format.R8G8B8A8_UNorm, wholeUav);
        Assert.Equal((int)Format.R8G8B8A8_UNorm_SRgb, wholeSrv);

        device.CreateUnorderedAccessView(albedo, null, new UnorderedAccessViewDescription { Format = (Format)explicitUav, ViewDimension = UnorderedAccessViewDimension.Texture2D }, heap.GetCPUDescriptorHandleForHeapStart());
        device.CreateShaderResourceView(albedo, new ShaderResourceViewDescription { Format = (Format)wholeSrv, ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D, Shader4ComponentMapping = ShaderComponentMapping.Default, Texture2D = new Texture2DShaderResourceView { MipLevels = 1 } }, heap.GetCPUDescriptorHandleForHeapStart() + (int)device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView));

        context.List.SetDescriptorHeaps(1, new[] { heap });
        var view = new UavView((int)Format.R8G8B8A8_UNorm, UavViews.Texture2D, Size, Size, 1);
        Assert.True(UavFill.Record(device.NativePointer, context.List.NativePointer, heap.GetGPUDescriptorHandleForHeapStart().Ptr, 0, view, new FillValue(Half, Half, Half, 0)));
        context.ExecuteAndWait();

        Assert.Equal(HalfBits & 0x00FFFFFF, FirstTexel(context, arena, albedo) & 0x00FFFFFF);
    }

    /// <summary>The format the shim gives a UAV (or RTV) of <paramref name="texture"/> in <paramref name="format"/>, or without a desc.</summary>
    private static int ViewFormat(ID3D12Resource texture, bool unordered, int? format)
    {
        byte* copy = stackalloc byte[TargetViewDescriptions.UnorderedAccessViewSize];
        int* description = stackalloc int[10];
        description[1] = (int)UnorderedAccessViewDimension.Texture2D;
        void* given = null;

        if (format.HasValue)
        {
            description[0] = format.Value;
            given = description;
        }

        void* view = unordered ? TargetViewDescriptions.UnorderedAccessView(texture.NativePointer, given, copy) : TargetViewDescriptions.RenderTargetView(texture.NativePointer, given, copy);

        return *(int*)view;
    }

    private static int ShaderViewFormat(ID3D12Resource texture)
    {
        byte* copy = stackalloc byte[DepthViewDescriptions.ShaderResourceViewSize];

        return *(int*)TargetViewDescriptions.ShaderResourceView(texture.NativePointer, null, copy);
    }

    private static uint FirstTexel(CommandContext context, ResourceArena arena, ID3D12Resource texture)
    {
        var readback = arena.ReadbackBuffer(Pitch * Size);
        var footprint = new PlacedSubresourceFootPrint { Footprint = new SubresourceFootPrint(Format.R8G8B8A8_Typeless, Size, Size, 1, Pitch) };
        context.List.ResourceBarrierTransition(texture, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
        context.List.CopyTextureRegion(new TextureCopyLocation(readback, footprint), 0, 0, 0, new TextureCopyLocation(texture, 0));
        context.ExecuteAndWait();

        void* mapped;
        readback.Map(0, null, &mapped).CheckError();
        uint texel = *(uint*)mapped;
        readback.Unmap(0);

        return texel;
    }
}
