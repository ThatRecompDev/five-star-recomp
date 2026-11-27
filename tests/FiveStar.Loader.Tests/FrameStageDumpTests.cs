// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>The <c>--dump-frame</c> stage dump: one readback per target and pass, in batches, mapped for viewing.</summary>
public sealed class FrameStageDumpTests
{
    private const int FloatRgba16 = 10;
    private const int Rgba = 4;

    [Fact]
    public void Targets_are_taken_once_per_pass_filtered_and_a_refused_one_waits_for_the_next_batch()
    {
        var dump = new FrameStageDump();
        FrameStageDump.Present = 2100;
        FrameStageDump.Max = 160;
        FrameStageDump.Filter = "TL_PS,Tonemap";
        FrameStageDump.Directory = Path.Combine(Path.GetTempPath(), "fivestar-stage-dump-test");

        Assert.Null(dump.Take(0x10, "TL_PS", 1920, 1080, FloatRgba16, depth: false, out _));
        Assert.NotNull(dump.Begin(2100, reportPending: false, written: 0));

        string? first = dump.Take(0x10, "sgd1_0b12_TL_PS PBR", 1920, 1080, FloatRgba16, depth: false, out int order);
        Assert.Equal("001-sgd1_0b12_TL_PS_PBR-1920x1080-fmt10.png", Path.GetFileName(first));
        Assert.Equal(1, order);
        Assert.Null(dump.Take(0x10, "sgd1_0b12_TL_PS PBR", 1920, 1080, FloatRgba16, depth: false, out _));
        Assert.Null(dump.Take(0x20, "PS_DeferredTextured", 1920, 1080, 27, depth: false, out _));
        Assert.Null(dump.Take(0x30, "Tonemap", 240, 135, FloatRgba16, depth: false, out _));

        // The readback cap refuses the tonemap: nothing more this frame, and the next batch takes it.
        Assert.NotNull(dump.Take(0x40, "Tonemap", 1920, 1080, 41, depth: true, out _));
        dump.Refused(0x40, "Tonemap");
        Assert.Null(dump.Take(0x50, "Tonemap", 1920, 1080, 28, depth: false, out _));

        Assert.Null(dump.Begin(2101, reportPending: true, written: 1));
        Assert.False(dump.IsOpen);
        Assert.Contains("batch 2", dump.Begin(2102, reportPending: false, written: 1));
        Assert.EndsWith("003-Tonemap-1920x1080-fmt41-depth.png", dump.Take(0x40, "Tonemap", 1920, 1080, 41, depth: true, out _));
        Assert.Null(dump.Begin(2103, reportPending: false, written: 2));
        Assert.Contains("done after 2 batches", dump.Begin(2104, reportPending: false, written: 2));

        FrameStageDump.Present = 0;
        FrameStageDump.Filter = null;

        // --dump-frame 2100, world (300 presents after the first world frame) or world+n.
        Assert.True(FrameStageDump.TryParseWhen("2100", out long present, out long offset));
        Assert.Equal((2100L, -1L), (present, offset));
        Assert.True(FrameStageDump.TryParseWhen("world", out _, out offset));
        Assert.Equal(FrameStageDump.DefaultWorldOffset, offset);

        Assert.True(FrameStageDump.TryParseWhen("world+150", out present, out offset));
        Assert.Equal((0L, 150L), (present, offset));
        Assert.False(FrameStageDump.TryParseWhen("world-3", out _, out _));
        Assert.False(FrameStageDump.TryParseWhen("0", out _, out _));
    }

    [Fact]
    public void Hdr_is_exposed_and_auto_exposed_nan_is_magenta_and_an_unchanged_target_hashes_the_same()
    {
        // RGBA16F texels: black, 1.0 (x/(1+x) = 0.5 before gamma), NaN.
        Half[] values = [(Half)0, (Half)0, (Half)0, (Half)1, (Half)1, (Half)1, (Half)1, (Half)1, Half.NaN, (Half)0, (Half)0, (Half)1];
        byte[] source = [.. values.SelectMany(value => BitConverter.GetBytes(value))];
        var image = new byte[3 * Rgba];

        string? stats = StagePixels.Convert(source, source.Length, 3, 1, FloatRgba16, depth: false, image, out float auto);
        Assert.Contains("exposure 1", stats);
        Assert.Contains("NaN 1", stats);
        Assert.Equal(new byte[] { 0, 0, 0, 255, 186, 186, 186, 255, 255, 0, 255, 255 }, image);

        // The finite means are 0.5 (red) and 1/3 (green, blue): auto exposure puts their mean at 0.18.
        Assert.Equal(0.18f / ((0.5f + (2f / 3f)) / 3f), auto, 3);
        Assert.True(StagePixels.Expose(source, source.Length, 3, 1, FloatRgba16, auto, image));
        Assert.Equal("a-auto.png", Path.GetFileName(StageReadback.AutoPath("a.png")));

        // Padding past a row's texels does not count; a changed texel does.
        ulong hash = StageReadback.Hash(source, source.Length, 3, 1, FloatRgba16);
        byte[] padded = [.. source, 7, 7];
        Assert.Equal(hash, StageReadback.Hash(padded, padded.Length, 3, 1, FloatRgba16));
        source[0] ^= 1;
        Assert.NotEqual(hash, StageReadback.Hash(source, source.Length, 3, 1, FloatRgba16));

        Assert.Equal(0, StagePixels.Level(-1f, signed: true, kind: 0));
        Assert.Equal(255, StagePixels.Level(1f, signed: true, kind: 0));
        Assert.Equal(0f, StagePixels.DepthGrey(0f, 0.001f, 5f));
        Assert.Equal(0.5f, StagePixels.DepthGrey(0.2f, 0.2f, 0f));
        Assert.Equal(0, StagePixels.TexelBytes(71));

        // A material-class depth shows its few values; the stencil plane goes beside it.
        var depths = new Dictionary<float, long> { [0f] = 3, [1f] = 1 };
        Assert.Equal("2 distinct values: 0 75.0%, 1 25.0%", StagePixels.CommonValues(depths, 4));
        Assert.Equal("075-TL_PS-1280x720-fmt19-stencil.png", Path.GetFileName(FrameStageDump.StencilPath("075-TL_PS-1280x720-fmt19-depth.png")));
        Assert.True(FrameStageDump.HasStencil(19));
    }
}
