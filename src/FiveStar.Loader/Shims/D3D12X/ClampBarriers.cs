// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Pipelines;
using FiveStar.Loader.Shims.D3D12X.Present;
using FiveStar.Loader.Shims.D3D12X.States;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// The barriers on a list's argument scratch (<see cref="IndirectClamp"/>, <see cref="ListIndirectScratch"/>). Legacy
/// transitions (the default) take three per ExecuteIndirect: COMMON to UNORDERED_ACCESS before the clamp, to
/// INDIRECT_ARGUMENT after it, and back to COMMON after the execution. The first waits for the previous indirect
/// execution, though that read another piece of the buffer, so no two indirect draws of a list overlap and each clamp
/// pass carries the drain (w95a: 10-15 ms a frame in "loader:indirect-clamp" on queue #1). With
/// <c>--clamp-enhanced-barriers</c>, where the device has enhanced barriers (D3D12_OPTIONS12), the scratch takes enhanced
/// buffer barriers only (a buffer has no layout, so its legacy state does not matter): one before the recording's first
/// clamp in the buffer (an earlier run on the queue read and copied it), and one per clamp from its compute writes to
/// the argument fetch, or to the amplification stage's read for records <see cref="MeshLaunch"/> launches. Pieces never
/// overlap within a recording, so nothing orders a clamp after the previous execution or launch. Every barrier after the
/// first names the scopes earlier ones ended in (<see cref="Earlier"/>), as the enhanced-barrier rules ask of a barrier
/// on the same buffer in one ExecuteCommandLists scope; VERTEX_SHADING covers the amplification and mesh stages. A
/// compute list names no graphics scope: the runtime refuses VERTEX_SHADING there at Close (E_INVALIDARG, issue #64),
/// and no launch reads a compute list's scratch.
/// </summary>
internal static unsafe class ClampBarriers
{
    /// <summary>ID3D12GraphicsCommandList7::Barrier and ID3D12CommandList::GetType (d3d12.h).</summary>
    private const int BarrierSlot = 80;
    private const int GetTypeSlot = 8;

    /// <summary>D3D12_COMMAND_LIST_TYPE_DIRECT.</summary>
    private const int DirectType = 0;

    /// <summary>D3D12_BARRIER_TYPE_BUFFER.</summary>
    private const uint BufferType = 2;

    /// <summary>D3D12_BARRIER_SYNC: VERTEX_SHADING (vertex to mesh stages), COMPUTE_SHADING, COPY, EXECUTE_INDIRECT.</summary>
    private const uint VertexSync = 0x8;
    private const uint ComputeSync = 0x80;
    private const uint CopySync = 0x200;
    private const uint IndirectSync = 0x800;

    /// <summary>D3D12_BARRIER_ACCESS: UNORDERED_ACCESS, SHADER_RESOURCE, INDIRECT_ARGUMENT, COPY_SOURCE.</summary>
    private const uint UnorderedAccess = 0x10;
    private const uint ShaderResourceAccess = 0x80;
    private const uint IndirectAccess = 0x200;
    private const uint CopySourceAccess = 0x800;

    /// <summary>D3D12_RESOURCE_STATE_COMMON, _UNORDERED_ACCESS, _NON_PIXEL_SHADER_RESOURCE, _INDIRECT_ARGUMENT and _COPY_SOURCE.</summary>
    private const int CommonState = 0;
    private const int UnorderedAccessState = 0x8;
    private const int ShaderReadState = 0x40;
    private const int IndirectArgumentState = 0x200;

    /// <summary>D3D12_OPTIONS12: MSPrimitivesPipelineStatisticIncludesCulledPrimitives, EnhancedBarriersSupported, RelaxedFormatCastingSupported.</summary>
    private const int Options12Words = 3;
    private const int EnhancedBarriersWord = 1;

    /// <summary>Whether <c>--clamp-enhanced-barriers</c> asks for enhanced barriers.</summary>
    public static bool Requested { get; set; }

    /// <summary>Whether the scratch takes enhanced barriers (asked for and the device has them).</summary>
    public static bool Enhanced { get; internal set; }

    /// <summary>The form in use, for the log.</summary>
    public static string Form
    {
        get
        {
            if (Enhanced)
            {
                return "enhanced barriers";
            }

            return "legacy transitions";
        }
    }

    /// <summary>When the clamp's program is made on <paramref name="device"/>: whether the scratch takes enhanced barriers.</summary>
    public static void Attach(nint device)
    {
        Enhanced = Requested && Supported(device);
    }

    /// <summary>Whether <paramref name="device"/> reports enhanced barriers.</summary>
    private static bool Supported(nint device)
    {
        int* options = stackalloc int[Options12Words];

        if (HResult.Failed(DesktopDevice.CheckFeatureSupport(device, (int)Vortice.Direct3D12.Feature.Options12, options, Options12Words * sizeof(int))))
        {
            return false;
        }

        return options[EnhancedBarriersWord] != 0;
    }

    /// <summary>Before a clamp writes its piece; <paramref name="first"/> for the recording's first clamp in the buffer.</summary>
    [HotPath]
    public static void BeforeClamp(nint list, nint scratch, bool first)
    {
        if (!Enhanced)
        {
            BarrierWriter.Transition(list, scratch, CommonState, UnorderedAccessState);

            return;
        }

        if (first)
        {
            Write(list, scratch, Earlier(list) | ComputeSync | CopySync, EarlierAccess(list) | UnorderedAccess | CopySourceAccess, ComputeSync, UnorderedAccess);
        }
    }

    /// <summary>After the zero pass of the budget counter, before the clamps charge it.</summary>
    [HotPath]
    public static void AfterZero(nint list, nint scratch)
    {
        if (!Enhanced)
        {
            var barrier = new TransitionBarrier { Type = TransitionBarrier.UavType, Resource = scratch };
            BarrierWriter.Write(list, &barrier);

            return;
        }

        Write(list, scratch, ComputeSync, UnorderedAccess, ComputeSync, UnorderedAccess);
    }

    /// <summary>
    /// After the clamp, before the execution fetches its records or, when <paramref name="launched"/>, the mesh launch
    /// stage reads them (<see cref="BeforeLaunch"/>).
    /// </summary>
    [HotPath]
    public static void AfterClamp(nint list, nint scratch, bool launched = false)
    {
        if (!Enhanced)
        {
            BarrierWriter.Transition(list, scratch, UnorderedAccessState, IndirectArgumentState);

            return;
        }

        if (launched)
        {
            Write(list, scratch, ComputeSync | Earlier(list), UnorderedAccess | EarlierAccess(list), VertexSync, ShaderResourceAccess);

            return;
        }

        Write(list, scratch, ComputeSync | Earlier(list), UnorderedAccess | EarlierAccess(list), IndirectSync, IndirectAccess);
    }

    /// <summary>Before the mesh launch stage reads clamped records: legacy transitions move the piece to a shader read; enhanced ones did at <see cref="AfterClamp"/>.</summary>
    [HotPath]
    public static void BeforeLaunch(nint list, nint scratch)
    {
        if (!Enhanced)
        {
            BarrierWriter.Transition(list, scratch, IndirectArgumentState, ShaderReadState);
        }
    }

    /// <summary>After those launches: legacy transitions go back to what <see cref="AfterExecute"/> expects.</summary>
    [HotPath]
    public static void AfterLaunch(nint list, nint scratch)
    {
        if (!Enhanced)
        {
            BarrierWriter.Transition(list, scratch, ShaderReadState, IndirectArgumentState);
        }
    }

    /// <summary>After the execution that read the piece: legacy transitions leave the buffer in COMMON for the next clamp.</summary>
    [HotPath]
    public static void AfterExecute(nint list, nint scratch)
    {
        if (!Enhanced)
        {
            BarrierWriter.Transition(list, scratch, IndirectArgumentState, CommonState);
        }
    }

    /// <summary>Before a copy reads a clamped piece (<c>--gpu-timings</c> work shapes), after <see cref="AfterExecute"/>.</summary>
    [HotPath]
    public static void BeforeCopy(nint list, nint scratch)
    {
        if (!Enhanced)
        {
            BarrierWriter.Transition(list, scratch, CommonState, TransitionBarrier.CopySourceState);

            return;
        }

        Write(list, scratch, ComputeSync | Earlier(list), UnorderedAccess | EarlierAccess(list), CopySync, CopySourceAccess);
    }

    /// <summary>After that copy.</summary>
    [HotPath]
    public static void AfterCopy(nint list, nint scratch)
    {
        if (!Enhanced)
        {
            BarrierWriter.Transition(list, scratch, TransitionBarrier.CopySourceState, CommonState);
        }
    }

    /// <summary>
    /// The scopes an earlier barrier of the recording on the scratch, or an earlier run on the queue, may have ended in
    /// (an argument fetch, and with mesh launches on a direct list the stage's read): a later barrier's SyncBefore must
    /// contain them.
    /// </summary>
    [HotPath]
    private static uint Earlier(nint list)
    {
        if (Launches(list))
        {
            return IndirectSync | VertexSync;
        }

        return IndirectSync;
    }

    /// <summary>The accesses of those scopes (each sync bit of a barrier needs an access it allows): the argument fetch, the stage's read.</summary>
    [HotPath]
    private static uint EarlierAccess(nint list)
    {
        if (Launches(list))
        {
            return IndirectAccess | ShaderResourceAccess;
        }

        return IndirectAccess;
    }

    /// <summary>Whether mesh launches may read the scratch of <paramref name="list"/>: with <c>--mesh-launch</c>, on a direct list.</summary>
    [HotPath]
    private static bool Launches(nint list)
    {
        return MeshLaunch.Enabled && ((delegate* unmanaged<nint, int>)ComObject.Slot(list, GetTypeSlot))(list) == DirectType;
    }

    /// <summary>One D3D12_BUFFER_BARRIER over the whole buffer (offset 0, size UINT64_MAX, as the API requires).</summary>
    private static void Write(nint list, nint buffer, uint syncBefore, uint accessBefore, uint syncAfter, uint accessAfter)
    {
        var barrier = new BufferBarrier
        {
            SyncBefore = syncBefore, SyncAfter = syncAfter, AccessBefore = accessBefore, AccessAfter = accessAfter, Resource = buffer, Offset = 0, Size = ulong.MaxValue,
        };
        var group = new BarrierGroup { Type = BufferType, Count = 1, Barriers = &barrier };
        ((delegate* unmanaged<nint, uint, BarrierGroup*, void>)ComObject.Slot(list, BarrierSlot))(list, 1, &group);
    }

    /// <summary>D3D12_BUFFER_BARRIER.</summary>
    private struct BufferBarrier
    {
        public uint SyncBefore;
        public uint SyncAfter;
        public uint AccessBefore;
        public uint AccessAfter;
        public nint Resource;
        public ulong Offset;
        public ulong Size;
    }

    /// <summary>D3D12_BARRIER_GROUP.</summary>
    private struct BarrierGroup
    {
        public uint Type;
        public uint Count;
        public BufferBarrier* Barriers;
    }
}
