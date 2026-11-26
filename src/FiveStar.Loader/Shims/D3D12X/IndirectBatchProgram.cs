// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

// The clamp pass over a job table filled while the list is recorded follows the 7900 XTX fork of VIctory (commit
// 0375962, crates/victory-gpu/src/indirect/batch.rs); GPL-3.0-or-later. Ported as facts and written anew here.

using System.Text;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// The compute program of <see cref="IndirectBatch"/>: one thread per job of the table (root view t4: the job count,
/// 12 bytes of nothing, then 16 bytes a job), each copying one record from one of four source buffers (t0 to t3) to its
/// slot in the list's scratch (u0) and clamping it with the bounds and the budget (u1) of <see cref="IndirectClamp"/>;
/// threads past the count return. A record of an ExecuteIndirect with a count buffer past the count is zeroed and charges
/// nothing. Reads past a source buffer's size (root constants) and slots past the scratch's size are skipped, so a table
/// overwritten in the ring (a list run again long after it was recorded) can only clamp wrong records, never read or
/// write outside the buffers. A job's first word: source (bits 0-1), kind (2-5), count source (6-7), whether counted
/// (8), the record's index in its ExecuteIndirect (9-15), its stride in words (16-31); then its offset in the source,
/// its slot's offset in the scratch, the count's offset in the count source.
/// </summary>
internal static unsafe class IndirectBatchProgram
{
    /// <summary>Root constants: zero the budget, the most jobs, the scratch's bytes, unused, the four sources' bytes.</summary>
    public const uint Constants = 8;
    public const uint ZeroConstant = 0;
    public const uint JobsConstant = 1;
    public const uint ScratchBytesConstant = 2;
    public const uint SourceBytesConstant = 4;

    /// <summary>Root parameters: the constants, the four sources, the table, the scratch, the budget counter.</summary>
    public const uint ConstantsParameter = 0;
    public const uint FirstSourceParameter = 1;
    public const uint TableParameter = 5;
    public const uint ScratchParameter = 6;
    public const uint BudgetParameter = 7;

    /// <summary>Bit positions in a job's first word.</summary>
    public const int KindShift = 2;
    public const int CountSourceShift = 6;
    public const int CountedShift = 8;
    public const int IndexShift = 9;
    public const int StrideShift = 16;

    /// <summary>The highest record index a job holds.</summary>
    public const uint MaxIndex = 127;

    /// <summary>Bytes of the table in front of the jobs, and of a job.</summary>
    public const uint HeaderBytes = 16;
    public const uint JobBytes = 16;

    private const uint GroupThreads = 64;
    private const int CreateComputePipelineSlot = 11;
    private const int CreateRootSignatureSlot = 16;

    private const string Source = """
        #define RS "RootConstants(num32BitConstants=8, b0), SRV(t0), SRV(t1), SRV(t2), SRV(t3), SRV(t4), UAV(u0), UAV(u1)"
        cbuffer Batch : register(b0) { uint zero; uint maxJobs; uint scratchBytes; uint unused; uint4 sourceBytes; };
        ByteAddressBuffer source0 : register(t0);
        ByteAddressBuffer source1 : register(t1);
        ByteAddressBuffer source2 : register(t2);
        ByteAddressBuffer source3 : register(t3);
        ByteAddressBuffer jobs : register(t4);
        RWByteAddressBuffer clamped : register(u0);
        RWByteAddressBuffer budget : register(u1);
        static const uint MaxVertices = 1u << 24;
        static const uint MaxVertexInstances = 1u << 26;
        static const uint MaxGroupsPerDimension = 65535u;
        static const uint MaxGroups = 1u << 20;
        static const uint ListBudget = 1u << 27;
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
        uint Load(uint source, uint at) {
            uint bytes = sourceBytes.w;
            if (source == 0u) bytes = sourceBytes.x;
            if (source == 1u) bytes = sourceBytes.y;
            if (source == 2u) bytes = sourceBytes.z;
            if (bytes < 4u || at > bytes - 4u) return 0u;
            if (source == 0u) return source0.Load(at);
            if (source == 1u) return source1.Load(at);
            if (source == 2u) return source2.Load(at);
            return source3.Load(at);
        }
        [RootSignature(RS)]
        [numthreads(64, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID) {
            if (zero != 0u) { if (id.x == 0u) budget.Store(0, 0u); return; }
            if (id.x >= min(jobs.Load(0), maxJobs)) return;
            uint4 job = jobs.Load4(16u + id.x * 16u);
            uint source = job.x & 3u;
            uint kind = (job.x >> 2) & 15u;
            uint stride = (job.x >> 16) * 4u;
            uint from = job.y;
            uint at = job.z;
            if (stride == 0u || at > scratchBytes || stride > scratchBytes - at) return;
            for (uint word = 0; word < stride; word += 4) clamped.Store(at + word, Load(source, from + word));
            bool counted = ((job.x >> 8) & 1u) != 0u;
            if (counted && ((job.x >> 9) & 127u) >= Load((job.x >> 6) & 3u, job.w)) {
                for (uint past = 0; past < stride; past += 4) clamped.Store(at + past, 0u);
                return;
            }
            uint3 asked = uint3(Load(source, from), Load(source, from + 4u), Load(source, from + 8u));
            if (kind <= 1u) {
                uint vertices = min(asked.x, MaxVertices);
                uint instances = min(asked.y, MaxVertexInstances / max(vertices, 1u));
                if (!Charge(vertices * instances)) instances = 0u;
                clamped.Store2(at, uint2(vertices, instances));
            } else if (kind == 2u) {
                uint3 groups = min(asked, uint3(MaxGroupsPerDimension, MaxGroupsPerDimension, MaxGroupsPerDimension));
                if (groups.z != 0u && groups.x * groups.y > MaxGroups / groups.z) groups = uint3(0u, 0u, 0u);
                if (!Charge(groups.x * groups.y * groups.z * 64u)) groups = uint3(0u, 0u, 0u);
                clamped.Store3(at, groups);
            } else {
                uint3 groups = asked;
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
    private static nint _root;
    private static nint _pipeline;
    private static bool _failed;

    /// <summary>The root signature and pipeline, made on first use on <paramref name="device"/>; false when they cannot be.</summary>
    public static bool Ready(nint device, out nint root, out nint pipeline)
    {
        if (_pipeline != 0)
        {
            (root, pipeline) = (_root, _pipeline);

            return true;
        }

        return Make(device, out root, out pipeline);
    }

    /// <summary>Thread groups for a table of <paramref name="jobs"/> jobs.</summary>
    public static uint Groups(uint jobs)
    {
        return (jobs + GroupThreads - 1) / GroupThreads;
    }

    private static bool Make(nint device, out nint root, out nint pipeline)
    {
        lock (Gate)
        {
            if (_pipeline == 0 && !_failed)
            {
                _failed = !Create(device);
            }

            (root, pipeline) = (_root, _pipeline);

            return _pipeline != 0;
        }
    }

    private static bool Create(nint device)
    {
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
        nint pipeline = 0;

        if (!HResult.Failed(hr))
        {
            nint* description = stackalloc nint[] { root, (nint)bytes, (nint)size, 0, 0, 0, 0 };
            Guid pipelineIid = PipelineIid;
            hr = ((delegate* unmanaged<nint, nint*, Guid*, nint*, int>)ComObject.Slot(device, CreateComputePipelineSlot))(device, description, &pipelineIid, &pipeline);
            QueueProgress.Name(pipeline, "loader:indirect-batch");
        }

        ComObject.Release(code);

        if (HResult.Failed(hr))
        {
            ShimContext.Runtime.Log.Warn($"[d3d12_xs] indirect-argument batch: no program ({HResult.Format(hr)}); records are clamped one by one");

            return false;
        }

        _root = root;
        _pipeline = pipeline;

        return true;
    }

    private static nint Compile()
    {
        byte[] source = Encoding.ASCII.GetBytes(Source);
        byte[] entry = Encoding.ASCII.GetBytes("main\0");
        byte[] target = Encoding.ASCII.GetBytes("cs_5_1\0");
        byte[] name = Encoding.ASCII.GetBytes("indirect_batch\0");

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
            ShimContext.Runtime.Log.Warn($"[d3d12_xs] indirect-argument batch: {Encoding.ASCII.GetString(message, (int)length).Trim()}");
            ComObject.Release(errors);
        }

        if (HResult.Failed(hr))
        {
            return 0;
        }

        return code;
    }
}
