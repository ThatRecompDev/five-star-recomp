// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Graphics;
using FiveStar.Graphics.Rendering;
using FiveStar.Loader.Shims.D3D12X.Resources;
using FiveStar.Loader.Tests.Support;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// The game's exposure statistics: a 5x1 RGBA32F target the GPU renders, read by the CPU through a Map of its memory.
/// On WARP the texels the GPU wrote land in that memory.
/// </summary>
public sealed unsafe class MapReadbackTests
{
    private const int Width = 5;
    private const int Channels = 4;
    private const int Looks = 500;
    private const int LookMilliseconds = 10;

    [WarpFact]
    public void A_mapped_one_row_target_gets_the_texels_the_gpu_wrote()
    {
        using var gpu = GpuDevice.CreateWarp(debugLayer: false);
        var device = gpu.Device;
        using var context = new CommandContext(device);
        using var heap = device.CreateDescriptorHeap(new Vortice.Direct3D12.DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 1));
        using var target = device.CreateCommittedResource(HeapType.Default, Vortice.Direct3D12.ResourceDescription.Texture2D(Format.R32G32B32A32_Float, Width, 1, 1, 1, flags: ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget);
        device.CreateRenderTargetView(target, null, heap.GetCPUDescriptorHandleForHeapStart());

        context.List.ClearRenderTargetView(heap.GetCPUDescriptorHandleForHeapStart(), new Vortice.Mathematics.Color4(0.022f, -5.5f, -5.5f, 1));
        context.ExecuteAndWait();

        var memory = new float[Width * Channels];

        fixed (float* game = memory)
        {
            Assert.True(MapReadback.Fill(device.NativePointer, target.NativePointer, (int)ResourceStates.RenderTarget, (byte*)game));
        }

        Assert.Equal([0.022f, -5.5f, -5.5f, 1f], memory[^Channels..]);
    }

    [WarpFact]
    public void A_later_map_gets_the_row_of_the_copy_an_earlier_map_started_after_the_work_it_waited_for()
    {
        using var gpu = GpuDevice.CreateWarp(debugLayer: false);
        var device = gpu.Device;
        using var context = new CommandContext(device);
        using var heap = device.CreateDescriptorHeap(new Vortice.Direct3D12.DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 1));
        using var target = device.CreateCommittedResource(HeapType.Default, Vortice.Direct3D12.ResourceDescription.Texture2D(Format.R32G32B32A32_Float, Width, 1, 1, 1, flags: ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget);
        device.CreateRenderTargetView(target, null, heap.GetCPUDescriptorHandleForHeapStart());

        using var fence = device.CreateFence(0);
        using var readback = new AsyncMapReadback();
        var memory = new float[Width * Channels];

        // The game's queue: the clear runs only once the CPU signals the fence it waits for, after the copy started.
        context.Queue.Wait(fence, 1).CheckError();
        context.List.ClearRenderTargetView(heap.GetCPUDescriptorHandleForHeapStart(), new Vortice.Mathematics.Color4(0.5f, 2f, 3f, 1));
        context.List.Close();
        context.Queue.ExecuteCommandList(context.List);
        context.Queue.Signal(fence, 2).CheckError();

        fixed (float* game = memory)
        {
            Assert.False(readback.TryServe(target.NativePointer, (byte*)game));
            readback.Start(device.NativePointer, target.NativePointer, (int)ResourceStates.RenderTarget, [(fence.NativePointer, 2)]);
            fence.Signal(1);

            for (int tries = 0; tries < Looks && !readback.TryServe(target.NativePointer, (byte*)game); tries++)
            {
                Thread.Sleep(LookMilliseconds);
            }
        }

        Assert.Equal([0.5f, 2f, 3f, 1f], memory[^Channels..]);
    }
}
