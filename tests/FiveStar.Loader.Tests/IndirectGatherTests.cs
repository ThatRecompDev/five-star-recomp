// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims;
using FiveStar.Loader.Shims.D3D12X;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Tests.Support;
using FiveStar.Shaders.Disassembly;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// --indirect-gather: records nothing earlier in their segment wrote are clamped by a prelude before the list runs, with
/// the clamp's bounds, on the default adapter; gathering ends at work or barriers that may write arguments.
/// </summary>
public sealed unsafe class IndirectGatherTests
{
    private const int CloseSlot = 9;
    private const int CopyBufferRegionSlot = 15;
    private const int MapSlot = 8;
    private const uint Stride = 12;
    private const uint Records = 3;
    private const nint Pipeline = 0x7001;

    [Fact]
    public void Gathered_dispatch_records_are_clamped_before_the_list_runs()
    {
        using var test = TestRuntime.Create();
        IndirectGather.Enabled = true;

        try
        {
            var device = new XboxDevice(test);
            var objects = ShimContext.Runtime.D3D12X.Objects;
            nint upload = device.Buffer(XboxDevice.UploadHeap, 256, XboxDevice.GenericRead);
            uint[] asked = [4, 5, 6, uint.MaxValue, uint.MaxValue, uint.MaxValue, 70000, 2, 1];
            asked.CopyTo(new Span<uint>(Map(upload), asked.Length));

            nint list = device.DirectList();
            IndirectGather.Reset((XboxObject*)list);
            var call = new IndirectCall(0x5170, IndirectClamp.DispatchKind, Stride, Records);
            Assert.True(IndirectGather.Try((XboxObject*)list, call, objects.Unwrap(upload), 0, 0, out nint gathered, out ulong slot));
            Assert.True(IndirectGather.Has(list));

            nint readback = device.Buffer(XboxDevice.ReadbackHeap, 256, XboxDevice.CopyDest);
            nint inner = ((XboxObject*)list)->Inner;
            ((delegate* unmanaged<nint, nint, ulong, nint, ulong, ulong, void>)XboxDevice.Vtable(inner)[CopyBufferRegionSlot])(inner, objects.Unwrap(readback), 0, gathered, slot, Stride * Records);
            Assert.Equal(0, ((delegate* unmanaged<nint, int>)XboxDevice.Vtable(list)[CloseSlot])(list));
            device.Run(list);

            uint* clamped = Map(readback);
            Assert.Equal([4u, 5u, 6u], new Span<uint>(clamped, 3).ToArray());
            Assert.Equal([0u, 0u, 0u], new Span<uint>(clamped + 3, 3).ToArray());
            Assert.Equal([65535u, 2u, 1u], new Span<uint>(clamped + 6, 3).ToArray());
            Assert.Equal(1, IndirectGatherPrelude.Count);
        }
        finally
        {
            IndirectGather.Enabled = false;
            IndirectGatherPrelude.Detach();
        }
    }

    [Fact]
    public void Gathering_ends_at_a_barrier_after_work_that_may_write_and_opens_again_in_a_new_segment()
    {
        IndirectGather.Enabled = true;
        var list = (XboxObject*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(XboxInterfaces.CommandListObjectSize);

        try
        {
            IndirectGather.Reset(list);
            Assert.True(IndirectGather.IsOpen(list));

            // A barrier before any work that may write changes nothing, nor does work of a pipeline that stores nothing.
            PipelineStores.Note(Pipeline, false);
            IndirectGather.Synced(list);
            IndirectGather.AfterWork(list, false, Pipeline);
            IndirectGather.Synced(list);
            Assert.True(IndirectGather.IsOpen(list));

            // Work of a pipeline not known to store nothing may write: the barrier after it ends gathering.
            IndirectGather.AfterWork(list, false, Pipeline + 1);
            Assert.True(IndirectGather.IsOpen(list));
            IndirectGather.Synced(list);
            Assert.False(IndirectGather.IsOpen(list));

            ListSegments.Of(list)->Count = 1;
            IndirectGather.SegmentStarted(list);
            Assert.True(IndirectGather.IsOpen(list));

            IndirectGather.AfterWork(list, true, Pipeline);
            IndirectGather.Synced(list);
            Assert.False(IndirectGather.IsOpen(list));

            // A new recording (its segments start over first, as in ResetList).
            ListSegments.Of(list)->Count = 0;
            IndirectGather.Reset(list);
            IndirectGather.Wrote(list);
            Assert.True(IndirectGather.IsOpen(list));
            IndirectGather.Close(list);
            Assert.False(IndirectGather.IsOpen(list));
        }
        finally
        {
            IndirectGather.Enabled = false;
            System.Runtime.InteropServices.NativeMemory.Free(list);
        }
    }

    [Fact]
    public void Programs_that_store_to_memory_are_told_from_those_that_only_read()
    {
        Assert.False(PipelineStores.Writes([Text("s_load_dwordx4 s[0:3], s[4:5], 0x0"), Text("buffer_load_dword v0, v1, s[0:3], 0 offen"), Text("ds_write_b32 v0, v1")]));
        Assert.True(PipelineStores.Writes([Text("buffer_load_dword v0, v1, s[0:3], 0 offen"), Text("buffer_store_dword v0, v1, s[0:3], 0 offen")]));
        Assert.True(PipelineStores.Writes([Text("image_atomic_add v0, v[1:2], s[0:7] dmask:0x1")]));
        Assert.True(PipelineStores.Writes([Text("global_store_dword v[0:1], v2, off")]));
    }

    private static Instruction Text(string text)
    {
        return new Instruction(0, sizeof(uint), text);
    }

    private static uint* Map(nint resource)
    {
        void* data = null;
        Assert.Equal(0, ((delegate* unmanaged<nint, uint, void*, void**, int>)XboxDevice.Vtable(resource)[MapSlot])(resource, 0, null, &data));

        return (uint*)data;
    }
}
