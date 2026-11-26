// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>What became of a slot-70 buffer view (<see cref="AddressBufferViews.Create"/>).</summary>
internal enum BufferViewOutcome
{
    /// <summary>Written as the game described it.</summary>
    Created,

    /// <summary>Written with its element count cut to what its desktop buffer holds (the rest reads 0, as past a console view's range).</summary>
    Clamped,

    /// <summary>Off its element size but on 16 bytes: written as a raw view over the same bytes.</summary>
    Raw,

    /// <summary>No desktop buffer covers its address.</summary>
    Uncovered,

    /// <summary>Off its element size and off 16 bytes.</summary>
    Misaligned,

    /// <summary>Its first element lies past its desktop buffer's end.</summary>
    PastEnd,

    /// <summary>A raw view whose first byte is off 16 bytes (D3D12_RAW_UAV_SRV_BYTE_ALIGNMENT).</summary>
    RawMisaligned,

    /// <summary>Not a buffer view, or one without an element size.</summary>
    NotBuffer,
}
