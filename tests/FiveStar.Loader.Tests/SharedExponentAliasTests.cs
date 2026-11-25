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
/// Shared-exponent light on WARP: a compute pass writes R9G9B9E5 bits into an R32_UINT texture of two levels and two
/// layers, and the copy puts them into its R9G9B9E5 shadow, which the hardware then reads as light.
/// </summary>
public sealed unsafe class SharedExponentAliasTests
{
    private const int Size = 8;
    private const int Layers = 2;
    private const int Levels = 2;

    /// <summary>D3D12_RESOURCE_STATE_UNORDERED_ACCESS; every state legal (a direct list).</summary>
    private const int UnorderedAccess = 0x8;
    private const int AnyState = -1;

    [WarpFact]
    public void The_shadow_holds_the_light_the_r32_uint_texture_was_written()
    {
        using var gpu = GpuDevice.CreateWarp(debugLayer: false);
        var device = gpu.Device;
        using var views = device.CreateDescriptorHeap(new Vortice.Direct3D12.DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, DescriptorHeapFlags.ShaderVisible));
        using var context = new CommandContext(device);
        using var arena = new ResourceArena(device);

        var description = Vortice.Direct3D12.ResourceDescription.Texture2D(Format.R32_UInt, Size, Size, Layers, Levels, flags: ResourceFlags.AllowUnorderedAccess);
        using var light = device.CreateCommittedResource(HeapType.Default, description, ResourceStates.UnorderedAccess);
        var uav = new UnorderedAccessViewDescription { Format = Format.R32_UInt, ViewDimension = UnorderedAccessViewDimension.Texture2DArray };
        uav.Texture2DArray.ArraySize = Layers;
        device.CreateUnorderedAccessView(light, null, uav, views.GetCPUDescriptorHandleForHeapStart());

        uint packed = FillValue.SharedExponent(1.0f, 0.5f, 0.25f);
        var list = context.List;
        list.SetDescriptorHeaps(1, new[] { views });
        var view = new UavView((int)Format.R32_UInt, UavViews.Texture2DArray, Size, Size, Layers);
        Assert.True(UavFill.Record(device.NativePointer, list.NativePointer, views.GetGPUDescriptorHandleForHeapStart().Ptr, 0, view, new FillValue(0, 0, 0, packed)));

        var console = new Shims.D3D12X.ResourceDescription { Dimension = (int)ResourceDimension.Texture2D, Width = Size, Height = Size, DepthOrArraySize = Layers, MipLevels = Levels, Format = (int)Format.R32_UInt };
        var source = new GpuTexture(0x1000, console, light.NativePointer, (int)Format.R32_UInt, UnorderedAccess, true, default);
        var alias = SharedExponentAliases.For(device.NativePointer, source);

        Assert.NotNull(alias);
        Assert.Equal(Levels * Layers, alias.Written.Length);
        Assert.Equal(SharedExponentCopy.PixelRead | SharedExponentCopy.NonPixelRead, SharedExponentCopy.Record(list.NativePointer, alias, UnorderedAccess, SharedExponentCopy.Common, AnyState));
        context.ExecuteAndWait();

        // Level 0 of both layers: subresources 0 and 2.
        Assert.All(ReadRow(device, context, arena, alias.Shadow, 0), texel => Assert.Equal(packed, texel));
        Assert.All(ReadRow(device, context, arena, alias.Shadow, Levels), texel => Assert.Equal(packed, texel));
    }

    [Fact]
    public void A_list_copies_again_after_it_used_the_source()
    {
        var alias = new SharedExponentAlias(null!, 0, 0, [], []);
        alias.Copied(1);

        Assert.True(alias.CurrentOn(1));
        Assert.False(alias.CurrentOn(2));

        alias.Used(1);

        Assert.False(alias.CurrentOn(1));
    }

    /// <summary>The first row of <paramref name="subresource"/> of the shadow (resting in both shader-resource states).</summary>
    private static uint[] ReadRow(ID3D12Device device, CommandContext context, ResourceArena arena, nint shadow, uint subresource)
    {
        System.Runtime.InteropServices.Marshal.AddRef(shadow);
        using var texture = new ID3D12Resource(shadow);
        Assert.Equal(Format.R9G9B9E5_SharedExp, texture.Description.Format);

        var footprints = new PlacedSubresourceFootPrint[1];
        device.GetCopyableFootprints(texture.Description, subresource, 1, 0, footprints, new uint[1], new ulong[1], out ulong total);
        var readback = arena.ReadbackBuffer((int)total);
        var read = ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource;

        context.List.ResourceBarrierTransition(texture, read, ResourceStates.CopySource, subresource);
        context.List.CopyTextureRegion(new TextureCopyLocation(readback, footprints[0]), 0, 0, 0, new TextureCopyLocation(texture, subresource));
        context.List.ResourceBarrierTransition(texture, ResourceStates.CopySource, read, subresource);
        context.ExecuteAndWait();

        void* mapped;
        readback.Map(0, null, &mapped).CheckError();
        var row = new uint[Size];
        new Span<uint>(mapped, Size).CopyTo(row);
        readback.Unmap(0);

        return row;
    }
}
