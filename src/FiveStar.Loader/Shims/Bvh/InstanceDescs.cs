// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using FiveStar.Loader.Shims.D3D12X.Resources;
using FiveStar.Shaders.Bvh;

namespace FiveStar.Loader.Shims.Bvh;

/// <summary>
/// Reads the D3D12_RAYTRACING_INSTANCE_DESCs (d3d12.h, 64 bytes: Transform[3][4], InstanceID:24 | InstanceMask:8,
/// InstanceContributionToHitGroupIndex:24 | Flags:8, AccelerationStructure) of a top-level build from game memory,
/// with the root and bounds of each BLAS from its header.
/// </summary>
internal static unsafe class InstanceDescs
{
    public const int Size = 64;

    private const int TransformFloats = 12;
    private const int IdOffset = 0x30;
    private const int ContributionOffset = 0x34;
    private const int BlasOffset = 0x38;
    private const uint LowBits = 0xFFFFFF;
    private const int TopByteShift = 24;

    // Header dword 0 of a built BLAS (docs/raytracing.md): low 16 bits format version 6, bit 24 (serialized, not yet
    // rehydrated) clear. The root in dword 12 is a box node (type 4 or 5) in every built structure.
    private const uint VersionMask = 0xFFFF;
    private const uint Version = 6;
    private const uint SerializedFlag = 0x01000000;

    /// <summary>
    /// Appends <paramref name="count"/> instances from the GPU address <paramref name="address"/> (an array of descs,
    /// or of their addresses when <paramref name="pointers"/>); false when a desc is unreadable. An instance whose BLAS
    /// header is unreadable or not that of a built structure (<see cref="IsBuilt"/>) gets no root, so the TLAS leaves
    /// it out.
    /// </summary>
    public static bool TryRead(ulong address, uint count, bool pointers, GameMemorySource memory, List<BvhInstance> instances)
    {
        ulong stride = pointers ? sizeof(ulong) : (ulong)Size;
        byte* array = memory.Find(address, stride * count);

        if (array == null)
        {
            return false;
        }

        for (uint i = 0; i < count; i++)
        {
            byte* desc = array + ((nint)i * (nint)stride);

            if (pointers)
            {
                desc = memory.Find(*(ulong*)desc, Size);
            }

            if (desc == null)
            {
                return false;
            }

            instances.Add(Read(desc, memory));
        }

        return true;
    }

    private static BvhInstance Read(byte* desc, GameMemorySource memory)
    {
        var transform = new ReadOnlySpan<float>(desc, TransformFloats).ToArray();
        uint id = *(uint*)(desc + IdOffset);
        uint contribution = *(uint*)(desc + ContributionOffset);
        ulong blas = *(ulong*)(desc + BlasOffset);

        uint root = AmdBvhLayout.InvalidNode;
        var bounds = BvhBounds.Empty;
        byte* header = blas == 0 ? null : memory.Find(blas, XdxrBlas.HeaderSize);

        if (header != null)
        {
            var bytes = new ReadOnlySpan<byte>(header, XdxrBlas.HeaderSize);

            if (IsBuilt(bytes))
            {
                root = XdxrBlas.Root(bytes);
                bounds = XdxrBlas.Bounds(bytes);
            }
        }

        return new BvhInstance(transform, id & LowBits, (byte)(id >> TopByteShift), contribution & LowBits, (byte)(contribution >> TopByteShift), blas, root, bounds);
    }

    /// <summary>
    /// Whether a header is that of a built (rehydrated or CPU-built) BLAS: version 6, not serialized, and a box root.
    /// Zeroed memory, a serialized structure still waiting for rehydration and an empty build fail it; their root
    /// (0, or a type the shaders would misread) must not enter a TLAS.
    /// </summary>
    public static bool IsBuilt(ReadOnlySpan<byte> header)
    {
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header);

        if ((flags & VersionMask) != Version || (flags & SerializedFlag) != 0)
        {
            return false;
        }

        uint type = XdxrBlas.Root(header) & AmdBvhLayout.NodeTypeMask;

        return type is AmdBvhLayout.Box16Type or AmdBvhLayout.Box32Type;
    }
}
