// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Graphics;
using FiveStar.Graphics.Rendering;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Tests.Support;
using Vortice.Direct3D12;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// <c>--gpu-upload-heaps</c> on WARP: off, a buffer keeps its heap; on, where the device has GPU upload heaps, the buffer
/// is made in one, the CPU writes it through its mapping and the GPU reads what was written; where it has none, the
/// buffer keeps its heap.
/// </summary>
[Collection(nameof(GpuUploadHeapsTests))]
public sealed unsafe class GpuUploadHeapsTests
{
    private const int Words = 64;
    private const int GenericRead = 0xAC3;

    [WarpFact]
    public void A_cpu_written_buffer_goes_to_a_gpu_upload_heap_only_with_the_flag_and_support()
    {
        using var gpu = GpuDevice.CreateWarp(debugLayer: false);
        nint device = gpu.Device.NativePointer;
        var usual = new Shims.D3D12X.Binding.HeapProperties { Type = Shims.D3D12X.Binding.HeapProperties.Upload };

        try
        {
            GpuUploadHeaps.Enabled = false;
            int state = GenericRead;
            Assert.Equal(usual.Type, GpuUploadHeaps.For(device, usual, ref state).Type);
            Assert.Equal(GenericRead, state);

            GpuUploadHeaps.Enabled = true;
            var chosen = GpuUploadHeaps.For(device, usual, ref state);

            if (!GpuUploadHeaps.Supported(device))
            {
                Assert.Equal(usual.Type, chosen.Type);

                return;
            }

            Assert.Equal(GpuUploadHeaps.GpuUpload, chosen.Type);
            Assert.Equal(GpuUploadHeaps.CommonState, state);
            Assert.Equal(Pattern(), ReadBack(gpu.Device, chosen, state));
        }
        finally
        {
            GpuUploadHeaps.Enabled = false;
        }
    }

    /// <summary>A buffer in <paramref name="properties"/>, written through its mapping, then copied by the GPU and read back.</summary>
    private static uint[] ReadBack(ID3D12Device device, Shims.D3D12X.Binding.HeapProperties properties, int state)
    {
        nint buffer = DesktopCalls.CreateBuffer(device.NativePointer, properties, Words * sizeof(uint), 0, state);
        Assert.NotEqual(0, buffer);
        var data = (uint*)DesktopCalls.Map(buffer);
        Pattern().CopyTo(new Span<uint>(data, Words));

        using var source = new ID3D12Resource(buffer);
        using var arena = new ResourceArena(device);
        using var context = new CommandContext(device);
        var readback = arena.ReadbackBuffer(Words * sizeof(uint));
        context.List.CopyBufferRegion(readback, 0, source, 0, Words * sizeof(uint));
        context.ExecuteAndWait();

        return [.. System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(ResourceArena.Read(readback, Words * sizeof(uint)))];
    }

    private static uint[] Pattern()
    {
        return [.. Enumerable.Range(0, Words).Select(index => 0x9E3779B9u * (uint)(index + 1))];
    }
}
