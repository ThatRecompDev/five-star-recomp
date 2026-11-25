// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using FiveStar.Loader.Shims.D3D12X.Resources;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>Draw probes read their readback buffers in place and keep the buffers alive under a cap.</summary>
public sealed unsafe class DrawProbeReadbackTests
{
    private const int Bgra8Unorm = 87;
    private const int Width = 3;
    private const int Height = 2;
    private const int RowPitch = 16;

    [Fact]
    public void A_float_target_reports_its_values_as_stored_and_a_trace_filter_takes_several_names()
    {
        // A 1x1 R32G32B32A32_FLOAT exposure texel well above 1, which the RGBA8 summary would clamp.
        var texel = new byte[16];
        float[] values = [12.5f, 12.5f, 12.5f, 1f];

        for (int i = 0; i < values.Length; i++)
        {
            BitConverter.TryWriteBytes(texel.AsSpan(i * sizeof(float)), values[i]);
        }

        Assert.Equal("; float mean (12.5, 12.5, 12.5, 1), max (12.5, 12.5, 12.5, 1), min (12.5, 12.5, 12.5, 1)", DrawProbes.FloatSummary(texel, 16, 1, 1, 2));

        // R16_FLOAT (the scattering distance volume), slices read one below the other: 0, 2, 4, 6 over two rows of two.
        byte[] halves = [.. new[] { (Half)0, (Half)2, (Half)4, (Half)6 }.SelectMany(value => BitConverter.GetBytes(value))];
        Assert.Equal("; float mean (3), max (6), min (0)", DrawProbes.FloatSummary(halves, 4, 2, 2, 54));
        Assert.Equal(string.Empty, DrawProbes.FloatSummary(texel, 16, 1, 1, Bgra8Unorm));

        Assert.True(DrawTrace.MatchesFilter("sgd1_06e1f075_t32_PS_CalculateExposure_396dfccc_Wrapped", "PassthroughComposite, CalculateExposure"));
        Assert.False(DrawTrace.MatchesFilter("sgd1_0b12c1ad_t32_TL_PS_PBR_MATERIAL_STANDARD_42ad1e50_Wrapped", "PassthroughComposite,CalculateExposure"));
    }

    [Fact]
    public void A_probe_reads_only_the_texture_the_trace_names_at_a_handle()
    {
        const nint Handle = 0x1000;
        const nint Texture = 0x2000;
        const int ShaderResource = 0xC0;
        var views = new TracedViews();

        views.Write(Handle, "SRV of 0x2000 2560x1440 format 19, view format 22, plane 1", Texture, ShaderResource, plane: 1);
        Assert.True(views.TryTexture(Handle, out var texture));
        Assert.Equal((Texture, ShaderResource, 1), texture);

        // An unwrapped SRV over the same handle: the trace shows it, and the probe no longer reads the old texture.
        views.Write(Handle, "SRV of 0x3000 60x34 format 54");
        Assert.True(views.TryText(Handle, out string text));
        Assert.Equal("SRV of 0x3000 60x34 format 54", text);
        Assert.False(views.TryTexture(Handle, out _));
    }

    /// <summary>A stencil plane's copy reads as the material ids TL_PS sees: the commonest values and their share.</summary>
    [Fact]
    public void A_stencil_plane_reads_as_a_histogram_of_its_values()
    {
        const int R8Uint = 62;
        const int RowPitch = 8;

        // 4x2 stencil texels in rows 8 bytes apart: six of material 0x10, two of 0x02.
        byte[] texels = [0x10, 0x10, 0x10, 0x02, 0xEE, 0xEE, 0xEE, 0xEE, 0x10, 0x10, 0x10, 0x02, 0xEE, 0xEE, 0xEE, 0xEE];

        Assert.Equal("; stencil 2 values: 0x10 75.0%, 0x02 25.0%", DrawProbes.StencilSummary(texels, RowPitch, 4, 2, R8Uint));
        Assert.Equal(string.Empty, DrawProbes.StencilSummary(texels, RowPitch, 4, 2, 61));

        // TL_PS's 0x6c0 record in w89 (a type dword of 1) names heap index 0x406a as the trace prints it; a T# names none.
        uint* stencil = stackalloc uint[] { 0x406a, 0, 0, 1, 0x406a, 0, 0, 0 };
        uint* image = stackalloc uint[] { 0x00168dc0, 0x000002b9, 0x01ff00ff, 0x90000000, 0, 0, 0, 0 };
        Assert.Equal(0x406au, TraceRecords.HeapIndex(stencil));
        Assert.Null(TraceRecords.HeapIndex(image));

        // The stencil plane of a 2D depth-stencil with 1 mip and 1 slice is subresource 1; with 3 mips and 6 slices, 18.
        Assert.Equal(1u, DrawProbes.PlaneSubresource(1, 1, 1));
        Assert.Equal(18u, DrawProbes.PlaneSubresource(1, 3, 6));
    }

    /// <summary>An R32_UINT copy tells float words (a -1.0 written into TL_PS's packed light) from zeros and small packed words.</summary>
    [Fact]
    public void A_uint_target_reports_its_float_like_words()
    {
        const int R32Uint = 42;
        uint[] words = [0, 0, 0xBF800000, 0x00012345];
        byte[] bytes = new byte[words.Length * sizeof(uint)];
        Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);

        string summary = DrawProbes.UintSummary(bytes, bytes.Length, words.Length, 1, R32Uint);

        Assert.StartsWith("; uint zero 50.0%, float-like 25.0%, sign set 25.0%; commonest 0x00000000 50.0%", summary, StringComparison.Ordinal);
        Assert.Equal(string.Empty, DrawProbes.UintSummary(bytes, bytes.Length, words.Length, 1, 41));
    }

    /// <summary>The world trigger captures three frames 100 presents apart, starting with the present after the first world one.</summary>
    [Fact]
    public void The_world_trigger_captures_three_frames_after_the_world_arrives()
    {
        Assert.Equal([4231L, 4331L, 4431L], DrawTrace.WorldFrames(4230));
    }

    /// <summary>The watched target's shape: the lit HDR (2560x1440 RGBA16F) and nothing else of its size.</summary>
    [Fact]
    public void A_watched_target_shape_parses_and_matches_only_its_size_and_format()
    {
        var shape = TargetShape.Parse("2560x1440:10");

        Assert.NotNull(shape);
        Assert.True(shape.Value.Matches(2560, 1440, 10));
        Assert.False(shape.Value.Matches(2560, 1440, 28));
        Assert.Null(TargetShape.Parse("2560x1440"));
        Assert.Null(TargetShape.Parse("hdr"));
    }

    /// <summary>A 3D volume and a 2D view of its first slice read apart in the trace (the scattering distance volume).</summary>
    [Fact]
    public void A_traced_resource_shows_its_depth_slices_and_mips()
    {
        const int Texture2D = 3;
        const int Texture3D = 4;
        const int R16Float = 54;

        Assert.Equal("60x34 format 54, 3D depth 512", DrawTrace.DescribeShape(Texture3D, 60, 34, 512, 1, R16Float));
        Assert.Equal("60x34 format 54", DrawTrace.DescribeShape(Texture2D, 60, 34, 1, 1, R16Float));
        Assert.Equal("512x512 format 71, 6 slices, 9 mips", DrawTrace.DescribeShape(Texture2D, 512, 512, 6, 9, 71));
    }

    [Fact]
    public void A_mapped_readback_summarizes_and_converts_as_a_managed_copy_did()
    {
        // BGRA rows 16 bytes apart (12 bytes of texels, 4 of padding): one blue pixel at (2, 0), one red at (0, 1).
        var mapped = new byte[RowPitch * Height];
        mapped[(2 * 4) + 0] = 200;
        mapped[RowPitch + 2] = 100;

        var tight = new byte[Width * 4 * Height];
        mapped.AsSpan(0, Width * 4).CopyTo(tight);
        mapped.AsSpan(RowPitch, Width * 4).CopyTo(tight.AsSpan(Width * 4));
        var expected = ReadbackPixels.ToRgba(new ReadbackImage(Width, Height, Bgra8Unorm, Width * 4, tight))!;

        var image = new byte[expected.Length];
        string? summary = ReadbackPixels.Summarize(mapped, RowPitch, Width, Height, Bgra8Unorm, image);

        Assert.Equal(ReadbackPixels.Summarize(expected, Width, Height), summary);
        Assert.Equal("2 of 6 pixels not black, box (0,0)-(2,1), mean rgb (16, 0, 33)", summary);
        Assert.Equal(expected, image);

        Assert.Equal(summary, ReadbackPixels.Summarize(mapped, RowPitch, Width, Height, Bgra8Unorm, Span<byte>.Empty));
        Assert.Null(ReadbackPixels.Summarize(mapped, RowPitch, Width, Height, 1, Span<byte>.Empty));
    }

    [Fact]
    public void Float_targets_convert_every_colour_channel()
    {
        // R16G16B16A16_FLOAT (0.25, 0.5, 1.0, 1.0) and R11G11B10_FLOAT (0.5, 1.0, 0.25): each channel lands in its own byte.
        var half = new byte[8];
        ushort[] channels = [0x3400, 0x3800, 0x3C00, 0x3C00];

        for (int i = 0; i < channels.Length; i++)
        {
            BitConverter.TryWriteBytes(half.AsSpan(i * 2), channels[i]);
        }

        uint small = 0x380u | (0x3C0u << 11) | (0x1A0u << 22);

        Assert.Equal(new byte[] { 63, 127, 255, 255 }, ReadbackPixels.ToRgba(new ReadbackImage(1, 1, 10, 8, half)));
        Assert.Equal(new byte[] { 127, 255, 63, 255 }, ReadbackPixels.ToRgba(new ReadbackImage(1, 1, 26, 4, BitConverter.GetBytes(small))));
    }

    [Fact]
    public void Probes_past_512_MB_of_live_readback_buffers_are_skipped()
    {
        long target = 3840L * 2160 * 4;

        Assert.True(DrawProbes.Fits(0, target));
        Assert.True(DrawProbes.Fits(DrawProbes.LiveBytesCap - target, target));
        Assert.False(DrawProbes.Fits(DrawProbes.LiveBytesCap - target + 1, target));
    }
}
