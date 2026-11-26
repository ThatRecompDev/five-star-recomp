// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// The compute program of <see cref="IndirectGather"/>: one thread per record, each copying its record from the source
/// buffer at its own offset to its slot in the list's gather buffer and clamping it with the bounds and the budget of
/// <see cref="IndirectClamp"/> (kind 255 zeroes the budget). Up to <see cref="RecordsPerPass"/> records of one source
/// buffer, kind and stride go in one pass, their offsets as root constants, so a pass needs no table in memory.
/// </summary>
internal static unsafe class IndirectGatherProgram
{
    /// <summary>Records per pass: the root signature holds 4 header and 2 per record constants, plus three root views (64 dwords at most).</summary>
    public const int RecordsPerPass = 26;

    /// <summary>Root constants: kind, stride, records, unused, then source and slot offsets per record.</summary>
    public const uint HeaderConstants = 4;
    public const uint Constants = HeaderConstants + (2 * RecordsPerPass);

    /// <summary>Root parameters: the constants, the source buffer, the gather buffer, its budget counter.</summary>
    public const uint ConstantsParameter = 0;
    public const uint SourceParameter = 1;
    public const uint GatherParameter = 2;
    public const uint BudgetParameter = 3;

    /// <summary>The kind that zeroes the budget counter.</summary>
    public const uint ZeroKind = 255;

    private const uint GroupThreads = 64;
    private const int CreateComputePipelineSlot = 11;
    private const int CreateRootSignatureSlot = 16;

    private const string Source = """
        #define RS "RootConstants(num32BitConstants=56, b0), SRV(t0), UAV(u0), UAV(u1)"
        cbuffer Gather : register(b0) { uint kind; uint stride; uint records; uint unused; uint4 offsets[13]; };
        ByteAddressBuffer arguments : register(t0);
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
            uint4 pair = offsets[id.x >> 1];
            uint from = (id.x & 1u) != 0u ? pair.z : pair.x;
            uint at = (id.x & 1u) != 0u ? pair.w : pair.y;
            for (uint word = 0; word < stride; word += 4) clamped.Store(at + word, arguments.Load(from + word));
            if (kind <= 1u) {
                uint2 asked = arguments.Load2(from);
                uint vertices = min(asked.x, MaxVertices);
                uint instances = min(asked.y, MaxVertexInstances / max(vertices, 1u));
                if (!Charge(vertices * instances)) instances = 0u;
                clamped.Store2(at, uint2(vertices, instances));
            } else if (kind == 2u) {
                uint3 groups = min(arguments.Load3(from), uint3(MaxGroupsPerDimension, MaxGroupsPerDimension, MaxGroupsPerDimension));
                if (groups.z != 0u && groups.x * groups.y > MaxGroups / groups.z) groups = uint3(0u, 0u, 0u);
                if (!Charge(groups.x * groups.y * groups.z * 64u)) groups = uint3(0u, 0u, 0u);
                clamped.Store3(at, groups);
            } else {
                uint3 groups = arguments.Load3(from);
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
        lock (Gate)
        {
            if (_pipeline == 0 && !_failed)
            {
                _failed = !Make(device);
            }

            (root, pipeline) = (_root, _pipeline);

            return _pipeline != 0;
        }
    }

    /// <summary>Threads groups for <paramref name="records"/> records.</summary>
    public static uint Groups(uint records)
    {
        return (records + GroupThreads - 1) / GroupThreads;
    }

    private static bool Make(nint device)
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

        if (!HResult.Failed(hr))
        {
            nint* description = stackalloc nint[] { root, (nint)bytes, (nint)size, 0, 0, 0, 0 };
            Guid pipelineIid = PipelineIid;
            nint pipeline = 0;
            hr = ((delegate* unmanaged<nint, nint*, Guid*, nint*, int>)ComObject.Slot(device, CreateComputePipelineSlot))(device, description, &pipelineIid, &pipeline);
            _pipeline = pipeline;
            QueueProgress.Name(pipeline, "loader:indirect-gather");
        }

        ComObject.Release(code);
        _root = root;

        if (HResult.Failed(hr))
        {
            ShimContext.Runtime.Log.Warn($"[d3d12_xs] indirect-argument gather: no program ({HResult.Format(hr)}); records are clamped one by one");

            return false;
        }

        return true;
    }

    private static nint Compile()
    {
        byte[] source = Encoding.ASCII.GetBytes(Source);
        byte[] entry = Encoding.ASCII.GetBytes("main\0");
        byte[] target = Encoding.ASCII.GetBytes("cs_5_1\0");
        byte[] name = Encoding.ASCII.GetBytes("indirect_gather\0");

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
            ShimContext.Runtime.Log.Warn($"[d3d12_xs] indirect-argument gather: {Encoding.ASCII.GetString(message, (int)length).Trim()}");
            ComObject.Release(errors);
        }

        if (HResult.Failed(hr))
        {
            return 0;
        }

        return code;
    }
}
