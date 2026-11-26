// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>The <c>--dump-frame</c> stage dump: one readback per target and pass, named by order and pass, mapped for viewing.</summary>
public sealed class FrameStageDumpTests
{
    private const int FloatRgba16 = 10;
    private const int Rgba = 4;

    [Fact]
    public void Each_target_is_taken_once_per_pass_and_named_by_order_pass_size_and_format()
    {
        var dump = new FrameStageDump();
        FrameStageDump.Present = 2100;
        FrameStageDump.Directory = Path.Combine(Path.GetTempPath(), "fivestar-stage-dump-test");

        Assert.Null(dump.Take(0x10, "TL_PS", 1920, 1080, FloatRgba16, depth: false, out _));
        dump.Begin(2100);

        string? first = dump.Take(0x10, "sgd1_0b12_TL_PS PBR", 1920, 1080, FloatRgba16, depth: false, out int order);
        Assert.Equal("01-sgd1_0b12_TL_PS_PBR-1920x1080-fmt10.png", Path.GetFileName(first));
        Assert.Equal(1, order);
        Assert.Null(dump.Take(0x10, "sgd1_0b12_TL_PS PBR", 1920, 1080, FloatRgba16, depth: false, out _));
        Assert.Null(dump.Take(0x20, "mip", 240, 135, FloatRgba16, depth: false, out _));
        Assert.EndsWith("02-Tonemap-1920x1080-fmt41-depth.png", dump.Take(0x10, "Tonemap", 1920, 1080, 41, depth: true, out _));

        dump.Begin(2101);
        Assert.False(dump.IsOpen);
        FrameStageDump.Present = 0;
    }

    [Fact]
    public void Hdr_is_exposed_signed_floats_are_mapped_and_nan_is_magenta()
    {
        // RGBA16F texels: black, 1.0 (x/(1+x) = 0.5 before gamma), NaN.
        Half[] values = [(Half)0, (Half)0, (Half)0, (Half)1, (Half)1, (Half)1, (Half)1, (Half)1, Half.NaN, (Half)0, (Half)0, (Half)1];
        byte[] source = [.. values.SelectMany(value => BitConverter.GetBytes(value))];
        var image = new byte[3 * Rgba];

        string? stats = StagePixels.Convert(source, source.Length, 3, 1, FloatRgba16, depth: false, image);
        Assert.Contains("exposure 1", stats);
        Assert.Contains("NaN 1", stats);
        Assert.Equal(new byte[] { 0, 0, 0, 255, 186, 186, 186, 255, 255, 0, 255, 255 }, image);

        // A normal in -1..1 is signed: -1 maps to 0, 1 to 255.
        Assert.Equal(0, StagePixels.Level(-1f, signed: true, kind: 0));
        Assert.Equal(255, StagePixels.Level(1f, signed: true, kind: 0));

        // Reverse-Z depth: the sky at 0.0 black, the nearest texel white, mid-grey for a flat depth.
        Assert.Equal(0f, StagePixels.DepthGrey(0f, 0.001f, 5f));
        Assert.Equal(1f, StagePixels.DepthGrey(0.001f * MathF.Exp(5f), 0.001f, 5f), 3);
        Assert.Equal(0.5f, StagePixels.DepthGrey(0.2f, 0.2f, 0f));
        Assert.Equal(4, StagePixels.TexelBytes(42));
        Assert.Equal(0, StagePixels.TexelBytes(71));
    }
}
