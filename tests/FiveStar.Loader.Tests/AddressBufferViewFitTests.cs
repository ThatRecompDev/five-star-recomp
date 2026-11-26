// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Shims.D3D12X.Resources;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// Slot-70 buffer views fitted to the desktop buffer over their memory: moved by their offset, cut at the buffer's end,
/// re-described raw when off their element size but on 16 bytes, refused when no view can read the same bytes.
/// </summary>
public sealed class AddressBufferViewFitTests
{
    private const ulong Size = 0x1000;
    private const uint Stride = 12;
    private const int R32Float = 41;

    [Fact]
    public void A_view_that_fits_moves_by_its_offset()
    {
        var view = Structured(first: 2, elements: 10);

        Assert.Equal(BufferViewOutcome.Created, AddressBufferViews.Fit(ref view, Stride, 24, Size));
        Assert.Equal(4ul, view.SrvFirstElement);
        Assert.Equal(10u, view.SrvElements);
    }

    [Fact]
    public void A_view_past_its_buffers_end_is_cut_to_the_elements_it_holds()
    {
        var view = Structured(first: 0, elements: 1000);

        Assert.Equal(BufferViewOutcome.Clamped, AddressBufferViews.Fit(ref view, Stride, 0xF00, Size));
        Assert.Equal(0xF00ul / Stride, view.SrvFirstElement);
        Assert.Equal((uint)((Size - 0xF00) / Stride), view.SrvElements);
    }

    [Fact]
    public void A_view_off_its_element_size_on_16_bytes_is_read_raw_over_the_same_bytes()
    {
        var view = Structured(first: 0, elements: 4);

        Assert.Equal(BufferViewOutcome.Raw, AddressBufferViews.Fit(ref view, Stride, 0x40, Size));
        Assert.Equal(BufferViewDescription.RawFormat, view.Format);
        Assert.Equal(BufferViewDescription.RawFlag, view.SrvFlags);
        Assert.Equal(0x40ul / sizeof(uint), view.SrvFirstElement);
        Assert.Equal(Stride * 4 / sizeof(uint), view.SrvElements);
    }

    [Fact]
    public void Views_no_desktop_view_can_read_the_same_bytes_of_are_refused()
    {
        var misaligned = Structured(first: 0, elements: 4);
        Assert.Equal(BufferViewOutcome.Misaligned, AddressBufferViews.Fit(ref misaligned, Stride, 0x44, Size));

        var past = Structured(first: 0, elements: 4);
        Assert.Equal(BufferViewOutcome.PastEnd, AddressBufferViews.Fit(ref past, Stride, Size + 8, Size));

        var raw = BufferViewDescription.RawShaderResource(4);
        Assert.Equal(BufferViewOutcome.RawMisaligned, AddressBufferViews.Fit(ref raw, sizeof(uint), 4, Size));

        var typed = new BufferViewDescription { Format = R32Float, Dimension = BufferViewDescription.BufferDimension, SrvElements = 4 };
        Assert.Equal(BufferViewOutcome.Created, AddressBufferViews.Fit(ref typed, sizeof(float), 4, Size));
        Assert.Equal(1ul, typed.SrvFirstElement);
    }

    private static BufferViewDescription Structured(ulong first, uint elements)
    {
        return new BufferViewDescription { Dimension = BufferViewDescription.BufferDimension, SrvFirstElement = first, SrvElements = elements, SrvStride = Stride };
    }
}
