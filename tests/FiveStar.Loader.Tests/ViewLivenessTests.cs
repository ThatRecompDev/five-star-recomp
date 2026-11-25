// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// --check-view-liveness: views noted where the shim writes and copies them follow their resource's last Release, and
/// a table record whose view is released, of the wrong access or a texture behind a buffer record is named.
/// </summary>
public sealed unsafe class ViewLivenessTests
{
    private const int Increment = 32;
    private const nint Texture = 0x5000;
    private const nint Buffer = 0x6000;

    [Fact]
    public void A_view_copied_from_a_desktop_heap_follows_its_resource_until_the_last_release()
    {
        nint desktopHeap = 0x10_0000;
        nint gameHeap = 0x20_0000;

        ViewLiveness.Written(desktopHeap, Texture, ViewShape.TextureSrv);
        ViewLiveness.Written(desktopHeap + Increment, Buffer, ViewShape.BufferUav);
        ViewLiveness.Copied(gameHeap, desktopHeap, 2, (uint)Increment);

        Assert.Equal((new SlotView(Texture, ViewShape.TextureSrv), true), ViewLiveness.Peek(gameHeap));
        ViewLiveness.Released(Texture);
        Assert.Equal((new SlotView(Texture, ViewShape.TextureSrv), false), ViewLiveness.Peek(gameHeap));

        // A slot written by a path that does not say what is not checked any more.
        ViewLiveness.Forget(gameHeap + Increment);
        Assert.Null(ViewLiveness.Peek(gameHeap + Increment));
    }

    [Theory]
    [InlineData((int)ViewShape.TextureSrv, false, false, false, (int)ViewFinding.Released)]
    [InlineData((int)ViewShape.TextureSrv, true, true, false, (int)ViewFinding.ReadOnlyInUavRange)]
    [InlineData((int)ViewShape.BufferUav, true, false, true, (int)ViewFinding.UavInReadOnlyRange)]
    [InlineData((int)ViewShape.TextureSrv, true, false, true, (int)ViewFinding.TextureBehindBufferRecord)]
    [InlineData((int)ViewShape.BufferSrv, true, false, true, (int)ViewFinding.None)]
    [InlineData((int)ViewShape.Null, false, true, true, (int)ViewFinding.None)]
    public void A_record_is_judged_by_its_views_resource_access_and_kind(int shape, bool live, bool uavRange, bool bufferRecord, int expected)
    {
        Assert.Equal((ViewFinding)expected, ViewLiveness.Classify(new SlotView(Texture, (ViewShape)shape), live, uavRange, bufferRecord));
    }

    [Fact]
    public void A_view_desc_says_buffer_or_texture_and_a_null_resource_is_a_null_view()
    {
        int* srv = stackalloc int[2];
        (srv[0], srv[1]) = (42, 1);

        Assert.Equal(ViewShape.BufferUav, ViewLiveness.Shape(Buffer, srv, unordered: true));
        srv[1] = 4;
        Assert.Equal(ViewShape.TextureSrv, ViewLiveness.Shape(Texture, srv, unordered: false));
        Assert.Equal(ViewShape.Null, ViewLiveness.Shape(0, null, unordered: false));
    }
}
