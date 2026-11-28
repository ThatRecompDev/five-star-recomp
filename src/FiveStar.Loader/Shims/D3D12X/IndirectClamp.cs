// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using FiveStar.Loader.Shims.D3D12X.Present;
using FiveStar.Loader.Shims.D3D12X.States;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// Bounds indirect arguments before the GPU runs them. The game's own compute passes write them; a pass that is
/// skipped (a stand-in pipeline) or translated wrongly leaves whatever the memory held, and a draw of four billion
/// vertices or a dispatch of 0xFFFFFFFF groups runs until the driver resets the device. Before an ExecuteIndirect with a
/// one-argument signature (all of the game's), a compute program of ours copies the records into the list's scratch
/// buffer (<see cref="ListIndirectScratch"/>) and clamps them, and the list executes the copy; the game's buffers are
/// not touched. Bounds: 2^24 vertices and 2^26 vertex instances per draw, 65,535 groups per dimension and 2^20 per
/// dispatch (more runs nothing), mesh dispatches one-dimensional with a first dimension over 65,535 run empty (a record
/// the game did not fill). A recording also has a budget of 2^27 work items (a vertex instance 1, a compute group
/// 64, a mesh group 128): records past it run empty. Each record alone stays within the bounds, but a list of
/// thousands of stale records (culling passes skipped for stand-ins) passed the driver's 5 s timeout (TDR at about
/// 300 s into a boot). The same mechanism and budget as victory-gta6's argument clamp (`indirect.rs`, `bound_ahead`).
/// </summary>
internal static unsafe class IndirectClamp
{
    /// <summary>D3D12_INDIRECT_ARGUMENT_TYPE_DRAW, _DRAW_INDEXED, _DISPATCH and _DISPATCH_MESH (d3d12.h).</summary>
    public const int DrawKind = 0;
    public const int DrawIndexedKind = 1;
    public const int DispatchKind = 2;
    public const int MeshKind = 10;

    /// <summary>Root parameters of the program: four constants, the arguments, the count, the copy.</summary>
    private const uint ConstantsParameter = 0;
    private const uint ArgumentsParameter = 1;
    private const uint CountParameter = 2;
    private const uint ClampedParameter = 3;
    private const uint BudgetParameter = 4;
    private const uint Constants = 4;
    private const uint ZeroKind = 255;
    private const uint GroupThreads = 64;

    /// <summary>ID3D12Device::CreateComputePipelineState and CreateRootSignature (d3d12.h).</summary>
    private const int CreateComputePipelineSlot = 11;
    private const int CreateRootSignatureSlot = 16;

    /// <summary>ID3D12GraphicsCommandList: Dispatch, SetPipelineState, SetComputeRootSignature, SetComputeRoot32BitConstants, SetComputeRootShaderResourceView, SetComputeRootUnorderedAccessView.</summary>
    private const int DispatchSlot = 14;
    private const int SetPipelineSlot = 25;
    private const int SetRootSignatureSlot = 29;
    private const int SetConstantsSlot = 35;
    private const int SetShaderResourceSlot = 39;
    private const int SetUnorderedAccessSlot = 41;

    /// <summary>Scratch pieces start on 256 bytes; the first buffer of a list holds 64 KiB.</summary>
    private const ulong Alignment = 256;
    private const ulong FirstScratchBytes = 64 << 10;
    private const ulong Growth = 2;

    /// <summary>D3D12_HEAP_TYPE_DEFAULT.</summary>
    private const int DefaultHeap = 1;

    // Work is counted as victory-gta6 does: a vertex instance is 1, a compute group 64, a mesh group 128. A record
    // whose work does not fit what is left of the list's budget runs empty and charges nothing; the charge is a
    // compare-exchange so the count never wraps. Kind 255 zeroes the list's counter.
    private const string Source = """
        #define RS "RootConstants(num32BitConstants=4, b0), SRV(t0), SRV(t1), UAV(u0), UAV(u1)"
        cbuffer Clamp : register(b0) { uint kind; uint stride; uint records; uint counted; };
        ByteAddressBuffer arguments : register(t0);
        ByteAddressBuffer countBuffer : register(t1);
        RWByteAddressBuffer clamped : register(u0);
        RWByteAddressBuffer budget : register(u1);
        static const uint MaxVertices = 1u << 24;
        static const uint MaxVertexInstances = 1u << 26;
        static const uint MaxGroupsPerDimension = 65535u;
        static const uint MaxGroups = 1u << 20;
        static const uint ListBudget = 1u << 27;
        static const uint ZeroKind = 255u;
        static const uint Tries = 64u;
        bool Charge(uint work) {
            if (work == 0u) return true;
            if (work > ListBudget) return false;
            uint seen;
            budget.InterlockedAdd(0, 0u, seen);
            [loop] for (uint i = 0; i < Tries; i++) {
                if (seen > ListBudget - work) return false;
                uint original;
                budget.InterlockedCompareExchange(0, seen, seen + work, original);
                if (original == seen) return true;
                seen = original;
            }
            return false;
        }
        [RootSignature(RS)]
        [numthreads(64, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID) {
            if (kind == ZeroKind) { if (id.x == 0u) budget.Store(0, 0u); return; }
            if (id.x >= records) return;
            uint at = id.x * stride;
            for (uint word = 0; word < stride; word += 4) clamped.Store(at + word, arguments.Load(at + word));
            uint live = records;
            if (counted != 0u) live = min(live, countBuffer.Load(0));
            if (id.x >= live) { for (uint past = 0; past < stride; past += 4) clamped.Store(at + past, 0u); return; }
            if (kind <= 1u) {
                uint2 asked = arguments.Load2(at);
                uint vertices = min(asked.x, MaxVertices);
                uint instances = min(asked.y, MaxVertexInstances / max(vertices, 1u));
                if (!Charge(vertices * instances)) instances = 0u;
                clamped.Store2(at, uint2(vertices, instances));
            } else if (kind == 2u) {
                uint3 groups = min(arguments.Load3(at), uint3(MaxGroupsPerDimension, MaxGroupsPerDimension, MaxGroupsPerDimension));
                if (groups.z != 0u && groups.x * groups.y > MaxGroups / groups.z) groups = uint3(0u, 0u, 0u);
                if (!Charge(groups.x * groups.y * groups.z * 64u)) groups = uint3(0u, 0u, 0u);
                clamped.Store3(at, groups);
            } else {
                uint3 groups = arguments.Load3(at);
                if (groups.x > MaxGroupsPerDimension) groups = uint3(0u, 0u, 0u);
                groups = uint3(groups.x, min(groups.y, 1u), min(groups.z, 1u));
                if (!Charge(groups.x * groups.y * groups.z * 128u)) groups = uint3(0u, 0u, 0u);
                clamped.Store3(at, groups);
            }
        }
        """;

    private static readonly object Gate = new();
    private static readonly Guid RootSignatureIid = new("c54a6b66-72df-4ee8-8be5-a946a1429214");
    private static readonly Guid PipelineIid = new("765a30f3-f624-4c6f-a828-ace948622445");
    private static nint _device;
    private static nint _root;
    private static nint _pipeline;
    private static bool _failed;
    private static long _clamped;

    /// <summary>ExecuteIndirect calls whose arguments were clamped.</summary>
    public static long Clamped => Interlocked.Read(ref _clamped);

    /// <summary>
    /// Records the clamp of <paramref name="maxCount"/> records of signature <paramref name="signature"/> from desktop
    /// buffer <paramref name="arguments"/> at <paramref name="argumentOffset"/> (bounded by the count at
    /// <paramref name="count"/>, when given) into the list's scratch; returns the scratch and its offset to execute
    /// instead, or false when the arguments run as they are. Replaces the list's compute root signature and pipeline:
    /// the caller sets its own again. Ends the list's open clamp batch first (<see cref="IndirectBatch.End"/>), so its
    /// piece is in the state this clamp's barriers expect. <paramref name="launched"/>: the mesh launch stage reads the
    /// clamped records (<see cref="LaunchClamped"/>), not an ExecuteIndirect.
    /// </summary>
    public static bool Record(XboxObject* self, nint signature, uint maxCount, nint arguments, ulong argumentOffset, nint count, ulong countOffset, out nint scratch, out ulong scratchOffset, bool launched = false)
    {
        scratch = 0;
        scratchOffset = 0;
        var (kind, stride) = CommandSignatures.Clampable(signature);
        nint list = self->Inner;
        IndirectBatch.End(self);

        if (kind < 0 || arguments == 0 || maxCount == 0 || !Ready())
        {
            return false;
        }

        ulong bytes = (ulong)stride * maxCount;

        // Each piece leaves room in front for the list's budget counter, used when the list has none in this buffer.
        if (bytes > uint.MaxValue || !Take(self, bytes + Alignment, out scratch, out scratchOffset))
        {
            return false;
        }

        ulong budget = BudgetAddress(self, scratch, scratchOffset, out bool zero);
        scratchOffset += Alignment;
        ulong destination = DesktopCalls.GpuAddress(scratch) + scratchOffset;
        uint counted = 0;
        ulong countAddress = DesktopCalls.GpuAddress(arguments) + argumentOffset;

        if (count != 0)
        {
            counted = 1;
            countAddress = DesktopCalls.GpuAddress(count) + countOffset;
        }

        if (NullAddressCheck.Enabled && (NullAddressCheck.Suspect(destination) || NullAddressCheck.Suspect(budget) || NullAddressCheck.Suspect(countAddress)))
        {
            return !NullAddressCheck.Drop(NullAddressKind.RootDescriptor, self, $"argument clamp views: clamped 0x{destination:x}, budget 0x{budget:x}, count 0x{countAddress:x}");
        }

        Begin(self, list, scratch, zero);

        ((delegate* unmanaged<nint, uint, ulong, void>)ComObject.Slot(list, SetShaderResourceSlot))(list, ArgumentsParameter, DesktopCalls.GpuAddress(arguments) + argumentOffset);
        ((delegate* unmanaged<nint, uint, ulong, void>)ComObject.Slot(list, SetShaderResourceSlot))(list, CountParameter, countAddress);
        ((delegate* unmanaged<nint, uint, ulong, void>)ComObject.Slot(list, SetUnorderedAccessSlot))(list, ClampedParameter, destination);
        ((delegate* unmanaged<nint, uint, ulong, void>)ComObject.Slot(list, SetUnorderedAccessSlot))(list, BudgetParameter, budget);

        if (zero)
        {
            DispatchZero(list);
            ClampBarriers.AfterZero(list, scratch);
        }

        uint* constants = stackalloc uint[] { (uint)kind, stride, maxCount, counted };
        ((delegate* unmanaged<nint, uint, uint, uint*, uint, void>)ComObject.Slot(list, SetConstantsSlot))(list, ConstantsParameter, Constants, constants, 0);
        ((delegate* unmanaged<nint, uint, uint, uint, void>)ComObject.Slot(list, DispatchSlot))(list, (maxCount + GroupThreads - 1) / GroupThreads, 1, 1);
        ClampBarriers.AfterClamp(list, scratch, launched);
        Interlocked.Increment(ref _clamped);
        GpuTimings.Work((nint)self, list, kind == DispatchKind, ListActivity.Of(self)->Pipeline);

        return true;
    }

    /// <summary>
    /// A piece of <paramref name="bytes"/> in the list's scratch for a clamp batch (<see cref="IndirectBatch"/>): the
    /// buffer, the piece's offset and the buffer's size, and the GPU address of the recording's budget counter, which
    /// the batch zeroes first when <paramref name="zero"/>. False when the clamp's program or the scratch cannot be made.
    /// </summary>
    internal static bool Piece(XboxObject* self, ulong bytes, out nint scratch, out ulong offset, out ulong scratchBytes, out ulong budget, out bool zero)
    {
        scratch = 0;
        offset = 0;
        scratchBytes = 0;
        budget = 0;
        zero = false;

        if (!Ready() || !Take(self, bytes + Alignment, out scratch, out offset))
        {
            return false;
        }

        budget = BudgetAddress(self, scratch, offset, out zero);
        offset += Alignment;
        scratchBytes = ListIndirectScratch.Of(self)->Bytes;

        return true;
    }

    /// <summary>
    /// The clamp's start: a pass of its own in <c>--gpu-timings</c> (the execution's pass starts after the clamp, at the
    /// end of <see cref="Record"/>), the scratch made writable (<paramref name="first"/>: the recording's first clamp in
    /// it, <see cref="ClampBarriers"/>) and our program bound.
    /// </summary>
    [HotPath]
    private static void Begin(XboxObject* self, nint list, nint scratch, bool first)
    {
        GpuTimings.Work((nint)self, list, compute: true, GpuTimings.IndirectClampPipeline);
        ClampBarriers.BeforeClamp(list, scratch, first);
        BindRoot(self, list);
        ((delegate* unmanaged<nint, nint, void>)ComObject.Slot(list, SetPipelineSlot))(list, _pipeline);
    }

    /// <summary>
    /// The program's root signature, unless the list still has it from the clamp before (an indirect draw leaves it
    /// bound: <see cref="CommandListBindings.ComputeDesktopRoot"/>). Its root views and constants are set by every clamp.
    /// </summary>
    [HotPath]
    private static void BindRoot(XboxObject* self, nint list)
    {
        var bindings = CommandListBindings.Of(self);

        if (bindings->ComputeDesktopRoot == CommandListBindings.ClampRoot)
        {
            return;
        }

        ((delegate* unmanaged<nint, nint, void>)ComObject.Slot(list, SetRootSignatureSlot))(list, _root);
        bindings->ComputeDesktopRoot = CommandListBindings.ClampRoot;
    }

    /// <summary>
    /// The address of the recording's budget counter; a recording without one in this buffer takes the room in front of
    /// <paramref name="piece"/> and must zero it (<paramref name="zero"/>).
    /// </summary>
    private static ulong BudgetAddress(XboxObject* self, nint scratch, ulong piece, out bool zero)
    {
        var state = ListIndirectScratch.Of(self);
        zero = state->Budget == 0;

        if (zero)
        {
            state->Budget = piece + 1;
        }

        return DesktopCalls.GpuAddress(scratch) + state->Budget - 1;
    }

    /// <summary>The zero pass of a budget counter (the list's first clamp in a buffer, or its first mesh launch).</summary>
    private static void DispatchZero(nint list)
    {
        uint* zero = stackalloc uint[] { ZeroKind, 0, 0, 0 };
        ((delegate* unmanaged<nint, uint, uint, uint*, uint, void>)ComObject.Slot(list, SetConstantsSlot))(list, ConstantsParameter, Constants, zero, 0);
        ((delegate* unmanaged<nint, uint, uint, uint, void>)ComObject.Slot(list, DispatchSlot))(list, 1, 1, 1);
    }

    /// <summary>The mesh launch budget's zero pass, then a wait for the store (UAV barrier).</summary>
    private static void ZeroBudget(nint list, nint buffer)
    {
        DispatchZero(list);

        var barrier = new TransitionBarrier { Type = TransitionBarrier.UavType, Resource = buffer };
        BarrierWriter.Write(list, &barrier);
    }

    /// <summary>After the ExecuteIndirect that read the scratch: what the next clamp expects (<see cref="ClampBarriers"/>).</summary>
    public static void Done(XboxObject* self, nint scratch)
    {
        ClampBarriers.AfterExecute(self->Inner, scratch);
    }

    /// <summary>Bytes of a list's mesh launch budget buffer: its counter, and room.</summary>
    private const ulong LaunchBudgetBytes = 256;

    /// <summary>
    /// The GPU address of the list's mesh launch budget (<see cref="MeshLaunch"/>), writable for the stage; 0 when it cannot
    /// be made. The recording's first launch zeroes it (this program's zero pass, then a UAV barrier); a later segment of
    /// the list, which runs as a list of its own after which the buffer is common again, makes it writable again. The
    /// buffer stays writable for the rest of the segment: the stage's draws charge it with atomics and nothing of ours
    /// waits between them.
    /// </summary>
    public static ulong LaunchBudget(XboxObject* self)
    {
        var state = ListIndirectScratch.Of(self);
        int segment = ListSegments.Of(self)->Count + 1;

        if (state->LaunchSegment == segment)
        {
            return DesktopCalls.GpuAddress(state->LaunchBudget);
        }

        if (!Ready())
        {
            return 0;
        }

        if (state->LaunchBudget == 0)
        {
            state->LaunchBudget = DesktopCalls.CreateBuffer(_device, new HeapProperties { Type = DefaultHeap }, LaunchBudgetBytes, DesktopCalls.UnorderedAccessFlag, ResourceStates.Common);

            if (state->LaunchBudget == 0)
            {
                return 0;
            }
        }

        nint list = self->Inner;
        nint buffer = state->LaunchBudget;
        ulong address = DesktopCalls.GpuAddress(buffer);
        BarrierWriter.Transition(list, buffer, ResourceStates.Common, ResourceStates.UnorderedAccess);

        if (state->LaunchSegment == 0)
        {
            BindRoot(self, list);
            ((delegate* unmanaged<nint, nint, void>)ComObject.Slot(list, SetPipelineSlot))(list, _pipeline);

            // The zero pass reads and writes nothing else; every root view names the budget.
            ((delegate* unmanaged<nint, uint, ulong, void>)ComObject.Slot(list, SetShaderResourceSlot))(list, ArgumentsParameter, address);
            ((delegate* unmanaged<nint, uint, ulong, void>)ComObject.Slot(list, SetShaderResourceSlot))(list, CountParameter, address);
            ((delegate* unmanaged<nint, uint, ulong, void>)ComObject.Slot(list, SetUnorderedAccessSlot))(list, ClampedParameter, address);
            ((delegate* unmanaged<nint, uint, ulong, void>)ComObject.Slot(list, SetUnorderedAccessSlot))(list, BudgetParameter, address);

            ZeroBudget(list, buffer);
            CommandListBindingMethods.RestoreCompute(self);
        }

        state->LaunchSegment = segment;

        return address;
    }

    /// <summary>
    /// After <see cref="Record"/> (launched) clamped <paramref name="records"/> mesh records of <paramref name="stride"/> bytes into
    /// <paramref name="scratch"/> at <paramref name="offset"/>, with the pipeline's mesh launch stage bound: each clamped
    /// record is launched (records past a count buffer's count were zeroed), and the scratch is left as <see cref="Done"/>
    /// expects it.
    /// </summary>
    public static void LaunchClamped(XboxObject* self, nint scratch, ulong offset, uint records, uint stride)
    {
        ulong budget = LaunchBudget(self);
        nint list = self->Inner;
        ulong first = DesktopCalls.GpuAddress(scratch) + offset;
        ClampBarriers.BeforeLaunch(list, scratch);

        // The stage does not charge a clamped record; its budget argument only has to be a writable buffer.
        for (uint record = 0; record < records; record++)
        {
            MeshLaunch.Record(list, MeshLaunch.ClampedRecord, first + ((ulong)record * stride), budget);
        }

        ClampBarriers.AfterLaunch(list, scratch);
    }

    /// <summary>The next aligned piece of <paramref name="bytes"/> in the list's scratch, growing it when full.</summary>
    private static bool Take(XboxObject* self, ulong bytes, out nint buffer, out ulong offset)
    {
        var scratch = ListIndirectScratch.Of(self);
        offset = (scratch->Used + Alignment - 1) & ~(Alignment - 1);

        if (scratch->Buffer == 0 || offset + bytes > scratch->Bytes)
        {
            ulong size = Math.Max(FirstScratchBytes, scratch->Bytes * Growth);

            while (size < bytes)
            {
                size *= Growth;
            }

            var properties = new HeapProperties { Type = DefaultHeap };
            nint grown = DesktopCalls.CreateBuffer(_device, properties, size, DesktopCalls.UnorderedAccessFlag, ResourceStates.Common);

            if (grown == 0)
            {
                buffer = 0;

                return false;
            }

            // The outgrown buffer stays alive: commands already recorded may read it. The budget starts over in the new one.
            scratch->Buffer = grown;
            scratch->Bytes = size;
            scratch->Budget = 0;
            offset = 0;
        }

        scratch->Used = offset + bytes;
        buffer = scratch->Buffer;

        return true;
    }

    /// <summary>The program and its root signature, made on first use.</summary>
    private static bool Ready()
    {
        if (_pipeline != 0)
        {
            return true;
        }

        lock (Gate)
        {
            if (_pipeline == 0 && !_failed)
            {
                _failed = !Make();
            }

            return _pipeline != 0;
        }
    }

    private static bool Make()
    {
        nint device = ShimContext.Runtime.D3D12X.Objects.DesktopDevice;
        nint code = Compile();

        if (device == 0 || code == 0)
        {
            return false;
        }

        byte* bytes = ((delegate* unmanaged<nint, byte*>)ComObject.Slot(code, IndirectClampNative.BlobPointerSlot))(code);
        nuint size = ((delegate* unmanaged<nint, nuint>)ComObject.Slot(code, IndirectClampNative.BlobSizeSlot))(code);
        Guid rootIid = RootSignatureIid;
        nint root = 0;
        int hr = ((delegate* unmanaged<nint, uint, byte*, nuint, Guid*, nint*, int>)ComObject.Slot(device, CreateRootSignatureSlot))(device, 0, bytes, size, &rootIid, &root);

        if (!HResult.Failed(hr))
        {
            var description = new ComputePipelineDescription { RootSignature = root, Code = bytes, CodeSize = size };
            Guid pipelineIid = PipelineIid;
            nint pipeline = 0;
            hr = ((delegate* unmanaged<nint, ComputePipelineDescription*, Guid*, nint*, int>)ComObject.Slot(device, CreateComputePipelineSlot))(device, &description, &pipelineIid, &pipeline);
            _pipeline = pipeline;
            QueueProgress.Name(pipeline, "loader:indirect-clamp");
        }

        ComObject.Release(code);

        if (HResult.Failed(hr))
        {
            ShimContext.Runtime.Log.Warn($"[d3d12_xs] indirect-argument clamp: no program ({HResult.Format(hr)}); arguments run unclamped");

            return false;
        }

        _device = device;
        _root = root;
        ClampBarriers.Attach(device);
        ShimContext.Runtime.Log.Info($"  [d3d12_xs] indirect-argument clamp: {ClampBarriers.Form} on the scratch");

        return true;
    }

    /// <summary>The program compiled by d3dcompiler_47 (cs_5_1, its root signature inside); 0 when that fails.</summary>
    private static nint Compile()
    {
        byte[] source = Encoding.ASCII.GetBytes(Source);
        byte[] entry = Encoding.ASCII.GetBytes("main\0");
        byte[] target = Encoding.ASCII.GetBytes("cs_5_1\0");
        byte[] name = Encoding.ASCII.GetBytes("indirect_clamp\0");

        nint code = 0;
        nint errors = 0;
        int hr;

        fixed (byte* text = source, main = entry, profile = target, file = name)
        {
            hr = IndirectClampNative.D3DCompile(text, (nuint)source.Length, file, 0, 0, main, profile, 0, 0, &code, &errors);
        }

        if (errors != 0)
        {
            byte* message = ((delegate* unmanaged<nint, byte*>)ComObject.Slot(errors, IndirectClampNative.BlobPointerSlot))(errors);
            nuint length = ((delegate* unmanaged<nint, nuint>)ComObject.Slot(errors, IndirectClampNative.BlobSizeSlot))(errors);
            ShimContext.Runtime.Log.Warn($"[d3d12_xs] indirect-argument clamp: {Encoding.ASCII.GetString(message, (int)length).Trim()}");
            ComObject.Release(errors);
        }

        if (HResult.Failed(hr))
        {
            return 0;
        }

        return code;
    }

    /// <summary>D3D12_COMPUTE_PIPELINE_STATE_DESC (d3d12.h).</summary>
    private struct ComputePipelineDescription
    {
        public nint RootSignature;
        public byte* Code;
        public nuint CodeSize;
        public uint NodeMask;
        public nint CachedBlob;
        public nuint CachedBlobSize;
        public uint Flags;
    }
}
