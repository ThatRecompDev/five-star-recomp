// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Graphics;
using FiveStar.Graphics.Rendering;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using FiveStar.Loader.Tests.Support;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// <c>--clear-targets-each-frame</c> on WARP: a render target holding last frame's contents is cleared at its first bind
/// of a new frame, and not again at a later bind of the same frame (what the frame drew stays).
/// </summary>
public sealed unsafe class TargetClearsTests
{
    private const int Size = 4;
    private const uint Red = 0xFF0000FF;

    [WarpFact]
    public void A_target_is_cleared_at_its_first_bind_in_a_frame_only()
    {
        using var gpu = GpuDevice.CreateWarp(debugLayer: false);
        var device = gpu.Device;
        using var context = new CommandContext(device);
        using var arena = new ResourceArena(device);
        using var heap = device.CreateDescriptorHeap(new Vortice.Direct3D12.DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 1));
        using var target = device.CreateCommittedResource(HeapType.Default, Vortice.Direct3D12.ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, Size, Size, 1, 1, flags: ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget);

        var handle = heap.GetCPUDescriptorHandleForHeapStart();
        device.CreateRenderTargetView(target, null, handle);

        long frame = 1;
        TargetClears.FrameSource = () => frame;
        TargetClears.ClearEachFrame = true;

        try
        {
            TargetClears.NoteView((nint)handle.Ptr, target.NativePointer, "test");
            nint view = (nint)handle.Ptr;
            var list = context.List;

            list.ClearRenderTargetView(handle, new Vortice.Mathematics.Color4(1, 0, 0, 1));
            TargetClears.Bound(list.NativePointer, 1, &view, single: false);
            Assert.Equal(0u, Read(context, arena, target));

            list.ClearRenderTargetView(handle, new Vortice.Mathematics.Color4(1, 0, 0, 1));
            TargetClears.Bound(list.NativePointer, 1, &view, single: false);
            Assert.Equal(Red, Read(context, arena, target));

            frame = 2;
            TargetClears.Bound(list.NativePointer, 1, &view, single: false);
            Assert.Equal(0u, Read(context, arena, target));
        }
        finally
        {
            TargetClears.ClearEachFrame = false;
        }
    }

    private static uint Read(CommandContext context, ResourceArena arena, ID3D12Resource target)
    {
        const int Pitch = 256;

        var readback = arena.ReadbackBuffer(Pitch * Size);
        var footprint = new PlacedSubresourceFootPrint { Footprint = new SubresourceFootPrint(Format.R8G8B8A8_UNorm, Size, Size, 1, Pitch) };
        context.List.ResourceBarrierTransition(target, ResourceStates.RenderTarget, ResourceStates.CopySource);
        context.List.CopyTextureRegion(new TextureCopyLocation(readback, footprint), 0, 0, 0, new TextureCopyLocation(target, 0));
        context.List.ResourceBarrierTransition(target, ResourceStates.CopySource, ResourceStates.RenderTarget);
        context.ExecuteAndWait();

        void* mapped;
        readback.Map(0, null, &mapped).CheckError();
        uint value = *(uint*)mapped;
        readback.Unmap(0);

        return value;
    }
}
