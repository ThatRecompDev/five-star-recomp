// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using FiveStar.Graphics;
using FiveStar.Graphics.Rendering;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using FiveStar.Loader.Tests.Support;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// --d3d12-nan-scan: a draw's float inputs are read back as the draw sees them and their NaN and Inf counted, with the
/// first one named; an input without any reads clean.
/// </summary>
public sealed unsafe class NanScanTests
{
    private const int Size = 4;
    private const int Texels = Size * Size;
    private const int Channels = 4;
    private const int NanTexel = 9;

    [WarpFact]
    public void A_texture_with_one_nan_texel_is_named_and_a_clean_one_is_not()
    {
        using var gpu = GpuDevice.CreateWarp(debugLayer: false);
        var device = gpu.Device;
        using var context = new CommandContext(device);
        using var dirty = Texture(device, withNan: true);
        using var clean = Texture(device, withNan: false);

        var dirtyCopy = NanScan.Copy(device.NativePointer, context.List.NativePointer, dirty.NativePointer, (int)ResourceStates.Common, "dirty");
        var cleanCopy = NanScan.Copy(device.NativePointer, context.List.NativePointer, clean.NativePointer, (int)ResourceStates.Common, "clean");
        context.ExecuteAndWait();

        Assert.NotNull(dirtyCopy);
        Assert.NotNull(cleanCopy);
        Assert.Equal(new FloatScanResult(1, NanTexel % Size, NanTexel / Size, 1, float.NaN), NanScan.Read(dirtyCopy));
        Assert.Equal(0, NanScan.Read(cleanCopy)!.Value.Count);

        dirtyCopy.Buffer.Dispose();
        cleanCopy.Buffer.Dispose();
    }

    [Fact]
    public void Half_packed_and_buffer_values_are_judged_and_block_compressed_formats_are_not_scanned()
    {
        // R16_FLOAT: +Inf (0x7c00) at texel 2; R11G11B10: a NaN blue (exponent 31, mantissa 1).
        byte[] half = [0, 0, 0, 0, 0x00, 0x7c];
        Assert.Equal(new FloatScanResult(1, 2, 0, 0, float.PositiveInfinity), FloatScan.Texture(half, half.Length, 3, 1, 54));

        var packed = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(packed, (0x1Fu << 5 | 1u) << 22);
        Assert.Equal(2, FloatScan.Texture(packed, packed.Length, 1, 1, 26)!.Value.Channel);

        var dwords = new byte[16];
        BinaryPrimitives.WriteSingleLittleEndian(dwords.AsSpan(8), float.NegativeInfinity);
        Assert.Equal((1, 8), (FloatScan.Dwords(dwords).Count, FloatScan.Dwords(dwords).X));

        Assert.True(FloatScan.BlockCompressed(95));
        Assert.Null(FloatScan.Texture(dwords, dwords.Length, 1, 1, 95));
    }

    /// <summary>
    /// --d3d12-nan-scan-dwords shows the rows a program reads of every buffer input, finite or not: PS_VelocityFill's
    /// previous view-projection (+0x1a0) with w's row all zero overflows the velocity target to Inf.
    /// </summary>
    [Fact]
    public void The_floats_a_program_reads_are_shown_from_the_offset_asked_for()
    {
        var bytes = new byte[0x1a0 + 16];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x1a0), 1.5f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x1ac), -2f);

        try
        {
            NanScan.Dwords = NanScan.ParseDwords("0x1a0:4");
            Assert.Equal("; floats at +0x1a0: 1.5, 0, 0, -2", NanScan.Values(bytes));

            NanScan.Dwords = NanScan.ParseDwords("512:4");
            Assert.Equal(string.Empty, NanScan.Values(bytes));
            Assert.Throws<ArgumentException>(() => NanScan.ParseDwords("0x1a0"));
        }
        finally
        {
            NanScan.Dwords = null;
        }
    }

    /// <summary>A 4x4 R32G32B32A32_FLOAT texture of 0.5s in CPU-written memory, with a NaN green at <see cref="NanTexel"/>.</summary>
    private static ID3D12Resource Texture(ID3D12Device device, bool withNan)
    {
        var heap = new HeapProperties(CpuPageProperty.WriteBack, MemoryPool.L0);
        var texture = device.CreateCommittedResource(heap, HeapFlags.None, ResourceDescription.Texture2D(Format.R32G32B32A32_Float, Size, Size, 1, 1), ResourceStates.Common);
        var texels = new float[Texels * Channels];
        Array.Fill(texels, 0.5f);

        if (withNan)
        {
            texels[(NanTexel * Channels) + 1] = float.NaN;
        }

        fixed (float* data = texels)
        {
            texture.WriteToSubresource(0, null, (nint)data, Size * Channels * sizeof(float), Texels * Channels * sizeof(float));
        }

        return texture;
    }
}
