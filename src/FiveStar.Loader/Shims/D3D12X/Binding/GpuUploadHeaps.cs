// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// <c>--gpu-upload-heaps</c> (an A/B, off by default): the buffers the CPU writes and every program reads, the descriptor
/// window, the address map and the user-data ring, go to a D3D12_HEAP_TYPE_GPU_UPLOAD heap (video memory the CPU writes
/// through the BAR, resizable BAR on) instead of write-combined system memory, which each GPU read crosses PCIe for.
/// Only where the device reports GPU upload heaps (D3D12_FEATURE_D3D12_OPTIONS16); elsewhere the buffers stay where
/// they were. After Kelvaroq's patches 0022 and 0039 (GPL-3.0, github.com/fablinoo/kelvaroq; THIRD_PARTY_NOTICES.md).
/// </summary>
internal static class GpuUploadHeaps
{
    /// <summary>D3D12_HEAP_TYPE_GPU_UPLOAD.</summary>
    public const int GpuUpload = 5;

    /// <summary>D3D12_RESOURCE_STATE_COMMON: a buffer in a GPU upload heap starts there and is promoted for reads.</summary>
    public const int CommonState = 0;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, bool> Support = new();

    /// <summary>Whether the window, the map and the ring ask for GPU upload heaps (<c>--gpu-upload-heaps</c>).</summary>
    public static bool Enabled { get; set; }

    /// <summary>Whether <paramref name="device"/> makes GPU upload heaps.</summary>
    public static bool Supported(nint device)
    {
        return Support.GetOrAdd(device, static pointer =>
        {
            Marshal.AddRef(pointer);
            using var desktop = new ID3D12Device(pointer);

            try
            {
                return desktop.CheckFeatureSupport<FeatureDataD3D12Options16>(Vortice.Direct3D12.Feature.Options16).GPUUploadHeapSupported;
            }
            catch (SharpGen.Runtime.SharpGenException)
            {
                return false;
            }
        });
    }

    /// <summary>
    /// The heap of a CPU-written buffer every program reads on <paramref name="device"/>: a GPU upload heap with the flag
    /// where the device has them (<paramref name="state"/> then COMMON), else <paramref name="usual"/> and its state.
    /// </summary>
    public static HeapProperties For(nint device, HeapProperties usual, ref int state)
    {
        if (!Enabled || !Supported(device))
        {
            return usual;
        }

        state = CommonState;

        return new HeapProperties { Type = GpuUpload };
    }
}
