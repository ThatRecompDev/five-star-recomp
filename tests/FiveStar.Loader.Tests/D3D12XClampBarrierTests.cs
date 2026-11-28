// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Shims;
using FiveStar.Loader.Shims.D3D12X;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Shims.D3D12X.States;
using FiveStar.Loader.Tests.Support;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// The argument scratch's barriers per clamped ExecuteIndirect or mesh launch: legacy transitions, or enhanced barriers
/// with none between one execution and the next clamp; a later barrier's SyncBefore holds the earlier SyncAfter.
/// </summary>
public sealed unsafe class D3D12XClampBarrierTests
{
    private const int Slots = 96;
    private const int ResourceBarrierSlot = 26;
    private const int BarrierSlot = 80;
    private const uint VertexSync = 0x8;
    private const uint ComputeSync = 0x80;
    private const uint IndirectSync = 0x800;
    private const uint IndirectAccess = 0x200;
    private const uint ShaderResourceAccess = 0x80;

    private const int CloseSlot = 9;
    private const int GetTypeSlot = 8;
    private const uint DispatchStride = 12;
    private const int DirectType = 0;
    private const int ComputeType = 2;
    private const int DefaultHeap = 1;

    private static int _transitions;
    private static int _barriers;
    private static uint _lastSyncBefore;
    private static uint _lastSyncAfter;
    private static uint _lastAccessBefore;
    private static uint _lastAccessAfter;
    private static uint _syncBefore;
    private static int _listType;

    [Fact]
    public void Legacy_transitions_take_three_barriers_per_execution()
    {
        Cycle(enhanced: false, first: false);

        Assert.Equal(3, _transitions);
        Assert.Equal(0, _barriers);
    }

    [Fact]
    public void Enhanced_barriers_take_one_per_clamp_and_none_after_the_execution()
    {
        Cycle(enhanced: true, first: false);

        Assert.Equal(0, _transitions);
        Assert.Equal(1, _barriers);
        Assert.Equal(ComputeSync | IndirectSync, _lastSyncBefore);
        Assert.Equal(IndirectSync, _lastSyncAfter);
        Assert.Equal(IndirectAccess, _lastAccessAfter);
    }

    [Fact]
    public void Legacy_launches_move_the_piece_to_a_shader_read_and_back()
    {
        Cycle(enhanced: false, first: false, launched: true);

        Assert.Equal(5, _transitions);
        Assert.Equal(0, _barriers);
    }

    [Fact]
    public void Enhanced_launches_take_one_barrier_from_the_clamps_writes_to_the_amplification_stages_read()
    {
        bool before = MeshLaunch.Enabled;
        MeshLaunch.Enabled = true;

        try
        {
            Cycle(enhanced: true, first: false, launched: true);
        }
        finally
        {
            MeshLaunch.Enabled = before;
        }

        Assert.Equal(0, _transitions);
        Assert.Equal(1, _barriers);
        Assert.Equal(ComputeSync | IndirectSync | VertexSync, _lastSyncBefore);
        Assert.Equal(0x10u | IndirectAccess | ShaderResourceAccess, _lastAccessBefore);
        Assert.Equal(VertexSync, _lastSyncAfter);
        Assert.Equal(ShaderResourceAccess, _lastAccessAfter);
    }

    [Fact]
    public void The_recordings_first_clamp_in_a_buffer_orders_it_after_earlier_runs()
    {
        Cycle(enhanced: true, first: true);

        Assert.Equal(0, _transitions);
        Assert.Equal(2, _barriers);
    }

    /// <summary>The runtime takes the enhanced sequence of a list with launches and executions (it refuses a barrier whose sync and access do not match at Close).</summary>
    [Fact]
    public void A_list_of_launched_and_executed_clamps_closes_with_enhanced_barriers()
    {
        using var test = TestRuntime.Create();
        bool enhanced = ClampBarriers.Enhanced;
        bool launch = MeshLaunch.Enabled;

        try
        {
            var device = new XboxDevice(test);
            var objects = ShimContext.Runtime.D3D12X.Objects;

            MeshLaunch.Enabled = true;
            ClampBarriers.Requested = true;
            ClampBarriers.Attach(objects.DesktopDevice);
            nint scratch = DesktopCalls.CreateBuffer(objects.DesktopDevice, new HeapProperties { Type = DefaultHeap }, 4096, DesktopCalls.UnorderedAccessFlag, ResourceStates.Common);
            Assert.NotEqual(0, scratch);

            var list = (XboxObject*)device.DirectList();
            nint inner = list->Inner;

            // A launched clamp, then one an ExecuteIndirect reads, then the work-shape copy.
            ClampBarriers.BeforeClamp(inner, scratch, first: true);
            ClampBarriers.AfterZero(inner, scratch);
            ClampBarriers.AfterClamp(inner, scratch, launched: true);
            ClampBarriers.BeforeLaunch(inner, scratch);
            ClampBarriers.AfterLaunch(inner, scratch);
            ClampBarriers.AfterExecute(inner, scratch);

            ClampBarriers.BeforeClamp(inner, scratch, first: false);
            ClampBarriers.AfterClamp(inner, scratch);
            ClampBarriers.AfterExecute(inner, scratch);

            ClampBarriers.BeforeCopy(inner, scratch);
            ClampBarriers.AfterCopy(inner, scratch);

            Assert.Equal(0, ((delegate* unmanaged<nint, int>)XboxDevice.Vtable((nint)list)[CloseSlot])((nint)list));
        }
        finally
        {
            ClampBarriers.Requested = false;
            ClampBarriers.Enhanced = enhanced;
            MeshLaunch.Enabled = launch;
        }
    }

    /// <summary>
    /// The clamps the game's ExecuteIndirects record with --clamp-enhanced-barriers --indirect-gather, on a direct and a
    /// compute list: a gathered record, two clamped alone, the work-shape copy. The runtime takes them at Close (it
    /// refused VERTEX_SHADING on a compute list in #64 and boot w406, whose driver later faulted in a Dispatch).
    /// </summary>
    [Theory]
    [InlineData(DirectType, false)]
    [InlineData(DirectType, true)]
    [InlineData(ComputeType, false)]
    [InlineData(ComputeType, true)]
    public void A_list_of_gathered_and_clamped_records_closes_with_enhanced_barriers(int type, bool meshLaunch)
    {
        using var test = TestRuntime.Create();
        bool enhanced = ClampBarriers.Enhanced;
        bool launch = MeshLaunch.Enabled;

        try
        {
            var device = new XboxDevice(test);
            var objects = ShimContext.Runtime.D3D12X.Objects;
            MeshLaunch.Enabled = meshLaunch;
            IndirectGather.Enabled = true;
            ClampBarriers.Requested = true;
            ClampBarriers.Attach(objects.DesktopDevice);

            var argument = new IndirectArgument { Type = IndirectArgument.DispatchType };
            var description = new CommandSignatureDescription { ByteStride = DispatchStride, ArgumentCount = 1, Arguments = &argument };
            nint signature = 0x5190 + type;
            CommandSignatures.Created(signature, &description, 0);
            nint upload = objects.Unwrap(device.Buffer(XboxDevice.UploadHeap, 256, XboxDevice.GenericRead));

            nint list = type == DirectType ? device.DirectList() : device.ComputeList();
            IndirectGather.Reset((XboxObject*)list);
            var call = new IndirectCall(signature, IndirectClamp.DispatchKind, DispatchStride, 2);
            Assert.True(IndirectGather.Try((XboxObject*)list, call, upload, 0, 0, out _, out _));

            for (int i = 0; i < 2; i++)
            {
                Assert.True(IndirectClamp.Record((XboxObject*)list, signature, 2, upload, 0, 0, 0, out nint scratch, out _));
                IndirectClamp.Done((XboxObject*)list, scratch);
                ClampBarriers.BeforeCopy(((XboxObject*)list)->Inner, scratch);
                ClampBarriers.AfterCopy(((XboxObject*)list)->Inner, scratch);
            }

            Assert.Equal(0, ((delegate* unmanaged<nint, int>)XboxDevice.Vtable(list)[CloseSlot])(list));
            Assert.False(LostListWrites.Has(list));
        }
        finally
        {
            ClampBarriers.Requested = false;
            ClampBarriers.Enhanced = enhanced;
            MeshLaunch.Enabled = launch;
            IndirectGather.Enabled = false;
            IndirectGatherPrelude.Detach();
        }
    }

    /// <summary>A compute list names no graphics scope, with --mesh-launch too: the runtime refused VERTEX_SHADING there at Close (#64).</summary>
    [Fact]
    public void A_compute_lists_barriers_name_no_vertex_scope()
    {
        bool before = MeshLaunch.Enabled;
        MeshLaunch.Enabled = true;

        try
        {
            Cycle(enhanced: true, first: true, type: ComputeType);
        }
        finally
        {
            MeshLaunch.Enabled = before;
        }

        Assert.Equal(2, _barriers);
        Assert.Equal(0u, _syncBefore & VertexSync);
        Assert.Equal(ComputeSync | IndirectSync, _lastSyncBefore);
        Assert.Equal(0x10u | IndirectAccess, _lastAccessBefore);
    }

    private static void Cycle(bool enhanced, bool first, bool launched = false, int type = DirectType)
    {
        _transitions = 0;
        _barriers = 0;
        _syncBefore = 0;
        _listType = type;

        var vtable = (nint*)NativeMemory.AllocZeroed(Slots, (nuint)sizeof(nint));
        var list = (nint*)NativeMemory.AllocZeroed(1, (nuint)sizeof(nint));
        bool before = ClampBarriers.Enhanced;

        try
        {
            vtable[ResourceBarrierSlot] = (nint)(delegate* unmanaged<nint, uint, void*, void>)&ResourceBarrier;
            vtable[BarrierSlot] = (nint)(delegate* unmanaged<nint, uint, uint*, void>)&Barrier;
            vtable[GetTypeSlot] = (nint)(delegate* unmanaged<nint, int>)&GetListType;
            *list = (nint)vtable;
            ClampBarriers.Enhanced = enhanced;

            ClampBarriers.BeforeClamp((nint)list, 0x1000, first);
            ClampBarriers.AfterClamp((nint)list, 0x1000, launched);

            if (launched)
            {
                ClampBarriers.BeforeLaunch((nint)list, 0x1000);
                ClampBarriers.AfterLaunch((nint)list, 0x1000);
            }

            ClampBarriers.AfterExecute((nint)list, 0x1000);
        }
        finally
        {
            ClampBarriers.Enhanced = before;
            NativeMemory.Free(list);
            NativeMemory.Free(vtable);
        }
    }

    [UnmanagedCallersOnly]
    private static int GetListType(nint self)
    {
        return _listType;
    }

    [UnmanagedCallersOnly]
    private static void ResourceBarrier(nint self, uint count, void* barriers)
    {
        _transitions += (int)count;
    }

    /// <summary>ID3D12GraphicsCommandList7::Barrier: one group of buffer barriers (SyncBefore, SyncAfter, AccessBefore, AccessAfter, ...).</summary>
    [UnmanagedCallersOnly]
    private static void Barrier(nint self, uint count, uint* groups)
    {
        _barriers += (int)count;
        var barrier = *(uint**)(groups + 2);
        _syncBefore |= barrier[0];

        _lastSyncBefore = barrier[0];
        _lastSyncAfter = barrier[1];
        _lastAccessBefore = barrier[2];
        _lastAccessAfter = barrier[3];
    }
}
