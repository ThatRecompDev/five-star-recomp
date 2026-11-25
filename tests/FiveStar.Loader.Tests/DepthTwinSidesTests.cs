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
/// Depth twins ordered by submission: each list notes the sides it uses, and at submission a prelude copies the side a
/// list needs from the other when the lists submitted before it left it behind, on a direct queue of the loader's own,
/// whatever queue the list runs on.
/// </summary>
[Collection("DepthTwins")]
public sealed unsafe class DepthTwinSidesTests
{
    private const int Size = 8;
    private const float Written = 0.25f;

    /// <summary>D3D12_RESOURCE_STATE_DEPTH_WRITE.</summary>
    private const int DepthWrite = 0x10;

    /// <summary>Bytes a row of the depth's copy takes in the readback (D3D12_TEXTURE_DATA_PITCH_ALIGNMENT).</summary>
    private const int Pitch = 256;

    [Fact]
    public void A_list_needs_the_side_it_uses_first_as_the_lists_before_it_left_it()
    {
        var twin = new DepthTwin(1, 2, DepthTwins.Float32, Size, Size);
        var copies = new List<DepthTwinSides.Copy>();

        // Recorded out of order: the reader first, then the writer, then a list that writes the twin after drawing.
        Assert.False(DepthTwinSides.UsePlane(101, twin, (int)Format.D32_Float));
        Assert.False(DepthTwinSides.UseTwin(102, twin));
        Assert.False(DepthTwinSides.UsePlane(103, twin, (int)Format.D32_Float));
        Assert.True(DepthTwinSides.UseTwin(103, twin));

        // Submitted in order: the writer (its new twin filled from the plane first), then the reader (the twin into
        // the plane first), then the third (the plane into the twin first: the reader's draw counts as a depth write).
        DepthTwinSides.Plan(102, copies);
        Assert.Equal([new DepthTwinSides.Copy(twin, false)], copies);

        copies.Clear();
        DepthTwinSides.Plan(101, copies);
        Assert.Equal([new DepthTwinSides.Copy(twin, true)], copies);

        copies.Clear();
        DepthTwinSides.Plan(103, copies);
        Assert.Empty(copies);
        Assert.Equal(DepthTwinSides.PlaneBehind, twin.Behind);
    }

    [WarpFact]
    public void Lists_recorded_out_of_order_see_the_twin_in_submission_order()
    {
        Run(computeWriter: false);
    }

    [WarpFact]
    public void A_first_writer_on_a_compute_queue_gets_its_twin_filled_by_the_prelude()
    {
        Run(computeWriter: true);
    }

    /// <summary>
    /// A reader (a copy of the depth plane) recorded before a writer (a fill of the twin), submitted after it: the reader
    /// sees what the writer wrote, through the prelude's copy into the plane.
    /// </summary>
    private static void Run(bool computeWriter)
    {
        DepthTwinCopy.Detach();
        DepthTwins.Detach();
        DepthTwinPrelude.Detach();

        using var gpu = GpuDevice.CreateWarp(debugLayer: false);
        var device = gpu.Device;
        using var setup = new CommandContext(device);
        using var arena = new ResourceArena(device);
        using var views = device.CreateDescriptorHeap(new Vortice.Direct3D12.DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, DescriptorHeapFlags.ShaderVisible));
        using var targets = device.CreateDescriptorHeap(new Vortice.Direct3D12.DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1));

        var description = Vortice.Direct3D12.ResourceDescription.Texture2D(Format.R32_Typeless, Size, Size, 1, 1, flags: ResourceFlags.AllowDepthStencil);
        using var depth = device.CreateCommittedResource(HeapType.Default, description, ResourceStates.DepthWrite, new ClearValue(Format.D32_Float, 1.0f, 0));
        device.CreateDepthStencilView(depth, new DepthStencilViewDescription { Format = Format.D32_Float, ViewDimension = DepthStencilViewDimension.Texture2D }, targets.GetCPUDescriptorHandleForHeapStart());
        setup.List.ClearDepthStencilView(targets.GetCPUDescriptorHandleForHeapStart(), ClearFlags.Depth, 1.0f, 0);
        setup.ExecuteAndWait();

        var twin = Twin(device, depth, description, views);
        long preludes = DepthTwinPrelude.Count;
        nint writerKey = computeWriter ? 202 : 201;
        nint readerKey = writerKey + 100;

        // The reader is recorded first, the writer second.
        Assert.False(DepthTwinSides.UsePlane(readerKey, twin, (int)Format.D32_Float));
        var readback = arena.ReadbackBuffer(Pitch * Size);
        using var reader = Reader(device, depth, readback);
        Assert.False(DepthTwinSides.UseTwin(writerKey, twin));
        using var writer = Writer(device, views, computeWriter);

        Submit(device, setup.Queue, writer, reader, writerKey, readerKey, computeWriter);

        Assert.Equal(preludes + 2, DepthTwinPrelude.Count);
        Assert.All(Floats(readback), value => Assert.Equal(Written, value));
    }

    private static DepthTwin Twin(ID3D12Device device, ID3D12Resource depth, Vortice.Direct3D12.ResourceDescription description, ID3D12DescriptorHeap views)
    {
        var desc = new Shims.D3D12X.ResourceDescription { Dimension = (int)description.Dimension, Width = description.Width, Height = description.Height, DepthOrArraySize = 1, MipLevels = 1, Format = (int)description.Format };
        var twin = DepthTwins.For(device.NativePointer, depth.NativePointer, desc, DepthTwins.Float32)!;
        System.Runtime.InteropServices.Marshal.AddRef(twin.Twin);
        using var resource = new ID3D12Resource(twin.Twin);
        device.CreateUnorderedAccessView(resource, null, new UnorderedAccessViewDescription { Format = Format.R32_Float, ViewDimension = UnorderedAccessViewDimension.Texture2D }, views.GetCPUDescriptorHandleForHeapStart());

        twin.Home = DepthWrite;
        twin.ViewHandle = (nint)views.GetCPUDescriptorHandleForHeapStart().Ptr;
        twin.ViewHeap = views.NativePointer;

        return twin;
    }

    /// <summary>A direct list that copies the depth plane into <paramref name="readback"/>.</summary>
    private static Lists Reader(ID3D12Device device, ID3D12Resource depth, ID3D12Resource readback)
    {
        var lists = new Lists(device, CommandListType.Direct);
        var footprint = new PlacedSubresourceFootPrint { Footprint = new SubresourceFootPrint(Format.R32_Typeless, Size, Size, 1, Pitch) };
        lists.List.ResourceBarrierTransition(depth, ResourceStates.DepthWrite, ResourceStates.CopySource);
        lists.List.CopyTextureRegion(new TextureCopyLocation(readback, footprint), 0, 0, 0, new TextureCopyLocation(depth, 0));
        lists.List.ResourceBarrierTransition(depth, ResourceStates.CopySource, ResourceStates.DepthWrite);
        lists.List.Close();

        return lists;
    }

    /// <summary>A list (direct or compute) that fills the twin through its UAV.</summary>
    private static Lists Writer(ID3D12Device device, ID3D12DescriptorHeap views, bool compute)
    {
        var type = CommandListType.Direct;

        if (compute)
        {
            type = CommandListType.Compute;
        }

        var lists = new Lists(device, type);
        lists.List.SetDescriptorHeaps(1, new[] { views });
        ulong table = views.GetGPUDescriptorHandleForHeapStart().Ptr;
        Assert.True(UavFill.Record(device.NativePointer, lists.List.NativePointer, table, 0, new UavView((int)Format.R32_Float, UavViews.Texture2D, Size, Size, 1), new FillValue(Written, Written, Written, 0)));
        lists.List.Close();

        return lists;
    }

    /// <summary>The writer, then the reader, each after its prelude; a compute writer runs on a compute queue the direct queue waits for.</summary>
    private static void Submit(ID3D12Device device, ID3D12CommandQueue direct, Lists writer, Lists reader, nint writerKey, nint readerKey, bool computeWriter)
    {
        using var compute = device.CreateCommandQueue(new Vortice.Direct3D12.CommandQueueDescription(CommandListType.Compute));
        using var fence = device.CreateFence(0);
        var writerQueue = computeWriter ? compute : direct;
        var copies = new List<DepthTwinSides.Copy>();

        DepthTwinSides.Plan(writerKey, copies);
        Assert.True(DepthTwinPrelude.Run(device.NativePointer, writerQueue.NativePointer, copies));
        writerQueue.ExecuteCommandList(writer.List);
        writerQueue.Signal(fence, 1);
        direct.Wait(fence, 1);

        copies.Clear();
        DepthTwinSides.Plan(readerKey, copies);
        Assert.True(DepthTwinPrelude.Run(device.NativePointer, direct.NativePointer, copies));
        direct.ExecuteCommandList(reader.List);
        direct.Signal(fence, 2);

        using var done = new AutoResetEvent(false);
        fence.SetEventOnCompletion(2, done.SafeWaitHandle.DangerousGetHandle());
        Assert.True(done.WaitOne(TimeSpan.FromSeconds(30)));
    }

    private static float[] Floats(ID3D12Resource readback)
    {
        void* mapped;
        readback.Map(0, null, &mapped).CheckError();
        var row = new float[Size];
        new Span<float>(mapped, Size).CopyTo(row);
        readback.Unmap(0);

        return row;
    }

    /// <summary>A command list with its own allocator.</summary>
    private sealed class Lists : IDisposable
    {
        public Lists(ID3D12Device device, CommandListType type)
        {
            Allocator = device.CreateCommandAllocator(type);
            List = device.CreateCommandList<ID3D12GraphicsCommandList>(0, type, Allocator, null);
        }

        public ID3D12CommandAllocator Allocator { get; }

        public ID3D12GraphicsCommandList List { get; }

        public void Dispose()
        {
            List.Dispose();
            Allocator.Dispose();
        }
    }
}
