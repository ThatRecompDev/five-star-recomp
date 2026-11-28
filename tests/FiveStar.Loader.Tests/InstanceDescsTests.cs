// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Shims;
using FiveStar.Loader.Shims.Bvh;
using FiveStar.Loader.Shims.D3D12X.Resources;
using FiveStar.Shaders.Bvh;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// The instance descs of a top-level build: an instance enters the TLAS with the root of its BLAS only when that BLAS
/// header is a built one; zeroed memory (whose dword 12 reads as node pointer 0), a serialized structure still waiting
/// for rehydration and a header without a box root leave the instance without a root.
/// </summary>
public sealed unsafe class InstanceDescsTests
{
    private const int BlasOffset = 0x38;
    private const int BlasSize = 0x200;
    private const int TrianglesOffset = 0x0C;
    private const uint SerializedFlag = 0x01000000;

    private static readonly float[] Positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
    private static readonly uint[] Indices = [0, 1, 2];

    [Fact]
    public void A_built_blas_gives_its_root_and_bounds()
    {
        var built = Build();
        var instance = Read(built);

        Assert.Equal(XdxrBlas.Root(built), instance.BlasRoot);
        Assert.Equal(XdxrBlas.Bounds(built), instance.BlasBounds);
    }

    [Fact]
    public void A_zeroed_blas_header_gives_no_root()
    {
        var instance = Read(new byte[BlasSize]);

        Assert.Equal(AmdBvhLayout.InvalidNode, instance.BlasRoot);
        Assert.Equal(BvhBounds.Empty, instance.BlasBounds);
    }

    [Fact]
    public void A_serialized_blas_header_gives_no_root()
    {
        var serialized = Build();
        BinaryPrimitives.WriteUInt32LittleEndian(serialized, BinaryPrimitives.ReadUInt32LittleEndian(serialized) | SerializedFlag);

        Assert.Equal(AmdBvhLayout.InvalidNode, Read(serialized).BlasRoot);
    }

    [Fact]
    public void A_root_that_is_not_a_box_node_gives_no_root()
    {
        var triangleRoot = Build();
        BinaryPrimitives.WriteUInt32LittleEndian(triangleRoot.AsSpan(XdxrBlas.RootOffset), AmdBvhLayout.NodePointer(XdxrBlas.HeaderSize, AmdBvhLayout.TriangleType));

        Assert.Equal(AmdBvhLayout.InvalidNode, Read(triangleRoot).BlasRoot);
    }

    [Fact]
    public void IsBuilt_accepts_a_box16_root_and_rejects_other_versions()
    {
        var header = Build();
        Assert.True(InstanceDescs.IsBuilt(header));

        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(XdxrBlas.RootOffset), AmdBvhLayout.NodePointer(XdxrBlas.HeaderSize, AmdBvhLayout.Box16Type));
        Assert.True(InstanceDescs.IsBuilt(header));

        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x80000005);
        Assert.False(InstanceDescs.IsBuilt(header));
    }

    private static byte[] Build()
    {
        var triangles = new List<BvhTriangle>();

        fixed (float* vertices = Positions)
        fixed (uint* indices = Indices)
        {
            var buffers = new TriangleBuffers(
                new ReadOnlySpan<byte>(vertices, Positions.Length * sizeof(float)), 12, 6, (uint)(Positions.Length / 3),
                new ReadOnlySpan<byte>(indices, Indices.Length * sizeof(uint)), 42, (uint)Indices.Length, [], 0, 0);
            TriangleDecoder.Append(triangles, buffers);
        }

        var blas = XdxrBlas.Build(triangles);
        Assert.NotEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(blas.AsSpan(TrianglesOffset)));

        return blas;
    }

    /// <summary>Reads one instance desc pointing at <paramref name="blas"/> through game-addressed memory.</summary>
    private static BvhInstance Read(byte[] blas)
    {
        using var physical = new XMemPhysicalMemory(new LoaderLog(TextWriter.Null, null, false));
        var memory = new GameMemorySource(physical);
        byte* block = (byte*)NativeMemory.AllocZeroed(InstanceDescs.Size + BlasSize);

        try
        {
            byte* dest = block + InstanceDescs.Size;
            blas.AsSpan().CopyTo(new Span<byte>(dest, BlasSize));
            *(ulong*)(block + BlasOffset) = (ulong)dest;

            var instances = new List<BvhInstance>();
            Assert.True(InstanceDescs.TryRead((ulong)block, 1, false, memory, instances));

            return Assert.Single(instances);
        }
        finally
        {
            NativeMemory.Free(block);
        }
    }
}
