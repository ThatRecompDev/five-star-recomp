// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// A compute ExecuteIndirect's argument records (D3D12_DISPATCH_ARGUMENTS at offset 0 of each) and count, in desktop
/// buffers; a capture copies them before the call and resolves the dispatches from them.
/// </summary>
internal sealed record IndirectArguments(nint Buffer, ulong Offset, uint Stride, uint Records, nint CountBuffer, ulong CountOffset)
{
    /// <summary>Where the records go in the readback buffer, the count dword after them.</summary>
    public ulong At { get; set; }

    public ulong Bytes => (ulong)Stride * Records;
}
