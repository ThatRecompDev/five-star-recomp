// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// Addresses the shim hands the desktop: an index or vertex view stays inside its desktop buffer (past it the GPU reads
/// unmapped memory, a page fault), one outside known memory is unbound, and an empty DRED page-fault output does not
/// read as a fault at GPU VA 0.
/// </summary>
public sealed class DesktopAddressTests
{
    private const ulong Game = 0x2522c570000;
    private const ulong Desktop = 0x5_0000_0000;
    private const ulong Bytes = 0x10000;

    [Fact]
    public void A_view_running_past_its_desktop_buffer_is_cut_to_it_and_one_outside_known_memory_is_unbound()
    {
        var range = new AddressRange(Game, Bytes, Desktop);

        var inside = InputBuffers.Fit(new VertexBufferView { Location = Game + 0x100, Size = 0x200, StrideOrFormat = 16 }, range);
        Assert.Equal((Desktop + 0x100, 0x200u, 16u), (inside.Location, inside.Size, inside.StrideOrFormat));

        var past = InputBuffers.Fit(new VertexBufferView { Location = Game + 0xFF00, Size = 0x1000 }, range);
        Assert.Equal((Desktop + 0xFF00, 0x100u), (past.Location, past.Size));

        var outside = InputBuffers.Fit(new VertexBufferView { Location = Game + Bytes, Size = 0x40 }, range);
        Assert.Equal((0UL, 0u), (outside.Location, outside.Size));
        Assert.Equal((0UL, 0u), (InputBuffers.Fit(new VertexBufferView { Location = Game, Size = 0x40 }, null).Location, 0u));
    }

    [Fact]
    public void An_empty_dred_page_fault_output_is_no_fault_at_va_0()
    {
        Assert.Contains("recorded no page fault", DredReport.PageFaultLine(0, 0, 0), StringComparison.Ordinal);
        Assert.Contains("page fault at GPU VA 0x0", DredReport.PageFaultLine(0, 0x1234, 0), StringComparison.Ordinal);
        Assert.Contains("page fault at GPU VA 0x5000", DredReport.PageFaultLine(0x5000, 0, 0), StringComparison.Ordinal);
    }
}
