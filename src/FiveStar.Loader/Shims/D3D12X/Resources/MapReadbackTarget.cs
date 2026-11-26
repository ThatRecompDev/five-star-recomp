// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// One mapped target of <see cref="AsyncMapReadback"/>: its readback buffer, the row the last finished copy brought back
/// and the copy in flight, if any.
/// </summary>
internal sealed class MapReadbackTarget : IDisposable
{
    public MapReadbackTarget(ID3D12Resource buffer, PlacedSubresourceFootPrint footprint, int rowBytes, ulong width, int format)
    {
        Buffer = buffer;
        Footprint = footprint;
        RowBytes = rowBytes;
        Width = width;
        Format = format;
        Row = new byte[rowBytes];
    }

    public ID3D12Resource Buffer { get; }

    public PlacedSubresourceFootPrint Footprint { get; }

    public int RowBytes { get; }

    /// <summary>The texture's width and format when the buffer was made: another texture at the same address starts over.</summary>
    public ulong Width { get; }

    public int Format { get; }

    /// <summary>The texels of the last finished copy.</summary>
    public byte[] Row { get; }

    /// <summary>Whether <see cref="Row"/> holds a finished copy.</summary>
    public bool HasRow { get; set; }

    /// <summary>The private queue's fence value the copy in flight signals; 0 when none is.</summary>
    public ulong Pending { get; set; }

    /// <summary>The texture the copy in flight reads, referenced until it finished.</summary>
    public ID3D12Resource? Source { get; set; }

    public void Dispose()
    {
        Source?.Dispose();
        Buffer.Dispose();
    }
}
