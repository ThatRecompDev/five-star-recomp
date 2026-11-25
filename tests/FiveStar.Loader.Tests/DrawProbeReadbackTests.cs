// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using FiveStar.Loader.Shims.D3D12X.Resources;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>Draw probes read their readback buffers in place and keep the buffers alive under a cap.</summary>
public sealed class DrawProbeReadbackTests
{
    private const int Bgra8Unorm = 87;
    private const int Width = 3;
    private const int Height = 2;
    private const int RowPitch = 16;

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
    public void Probes_past_512_MB_of_live_readback_buffers_are_skipped()
    {
        long target = 3840L * 2160 * 4;

        Assert.True(DrawProbes.Fits(0, target));
        Assert.True(DrawProbes.Fits(DrawProbes.LiveBytesCap - target, target));
        Assert.False(DrawProbes.Fits(DrawProbes.LiveBytesCap - target + 1, target));
    }
}
