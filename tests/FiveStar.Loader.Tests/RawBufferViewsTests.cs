// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.Binding;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// Buffer views at slots programs reach by heap index become raw (as programs read them), null where a raw view cannot
/// start; views whose record holds a console base stay as the game made them.
/// </summary>
public sealed unsafe class RawBufferViewsTests
{
    /// <summary>DXGI_FORMAT_R16_UINT, a typed index-buffer view.</summary>
    private const int R16Uint = 57;

    private static readonly BufferRecord Kept = new(2, 3072, 0x1100bfac);

    [Fact]
    public void A_kept_index_typed_or_structured_view_becomes_raw_over_the_same_bytes()
    {
        // 3,072 16-bit indices from element 8 (byte 16): 1,536 dwords from dword 4.
        var typed = new BufferViewDescription { Format = R16Uint, Dimension = BufferViewDescription.BufferDimension, SrvFirstElement = 8, SrvElements = 3072 };
        var raw = View(&typed, unordered: false, Kept, out nint resource);

        Assert.Equal((BufferViewDescription.RawFormat, BufferViewDescription.RawFlag, 4ul, 1536u, 0u), (raw.Format, raw.SrvFlags, raw.SrvFirstElement, raw.SrvElements, raw.SrvStride));
        Assert.Equal(1, resource);

        // A 28-byte structured UAV from element 4 (byte 112): raw, and without its counter.
        var structured = new BufferViewDescription { Dimension = BufferViewDescription.BufferDimension, UavFirstElement = 4, UavElements = 10, UavStride = 28 };
        ((ulong*)&structured)[3] = 64;
        var rawUav = View(&structured, unordered: true, Kept, out _);

        Assert.Equal((BufferViewDescription.RawFormat, 28ul, 70u, 0u), (rawUav.Format, rawUav.UavFirstElement, rawUav.UavElements, rawUav.UavStride));
        Assert.Equal(0ul, ((ulong*)&rawUav)[3]);
    }

    [Fact]
    public void A_view_that_cannot_start_raw_is_null_and_based_raw_or_switched_off_views_stay()
    {
        // Element 3 of 16-bit indices is byte 6, off the 16 bytes a raw view needs: a null raw view on no resource.
        var odd = new BufferViewDescription { Format = R16Uint, Dimension = BufferViewDescription.BufferDimension, SrvFirstElement = 3, SrvElements = 30 };
        var none = View(&odd, unordered: false, Kept, out nint resource);

        Assert.Equal((0, BufferViewDescription.RawFormat, 0u), (resource, none.Format, none.SrvElements));

        var typed = new BufferViewDescription { Format = R16Uint, Dimension = BufferViewDescription.BufferDimension, SrvElements = 8 };
        Assert.Equal(R16Uint, View(&typed, unordered: false, Kept with { Base = 0x2_8a43_b440 }, out _).Format);

        RawBufferViews.Enabled = false;

        try
        {
            Assert.Equal(R16Uint, View(&typed, unordered: false, Kept, out _).Format);
        }
        finally
        {
            RawBufferViews.Enabled = true;
        }
    }

    /// <summary>The view written for <paramref name="view"/> on resource 1 with <paramref name="record"/>.</summary>
    private static BufferViewDescription View(BufferViewDescription* view, bool unordered, BufferRecord record, out nint resource)
    {
        BufferViewDescription copy;
        resource = 1;

        return *(BufferViewDescription*)RawBufferViews.View(view, (byte*)&copy, unordered, record, ref resource);
    }
}
