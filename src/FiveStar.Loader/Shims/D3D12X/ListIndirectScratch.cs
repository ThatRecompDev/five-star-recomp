// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// A command list's device-local buffer for clamped indirect arguments (<see cref="IndirectClamp"/>), kept in its
/// wrapper: each ExecuteIndirect of a recording takes the next piece, and a Reset starts over (a list's next run follows
/// its last run in its queue's order, or waits for it on the GPU on another queue, <see cref="ListSubmissions"/>). A
/// buffer the list outgrows is not released, as commands
/// already recorded may use it; buffers only grow (doubling), so few are left behind.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ListIndirectScratch
{
    /// <summary>Offset in the command-list wrapper, past <see cref="ListMemoryWaits"/> (0x1600 to 0x1960).</summary>
    public const int Offset = 0x1A00;

    public nint Buffer;
    public ulong Bytes;
    public ulong Used;

    /// <summary>
    /// One past the offset of this recording's budget counter in <see cref="Buffer"/> (the work its clamped records
    /// may still ask for, <see cref="IndirectClamp"/>); 0 for none yet.
    /// </summary>
    public ulong Budget;

    /// <summary>The list's budget buffer for <see cref="MeshLaunch"/> (256 bytes, its counter in the first word), made on first use and kept.</summary>
    public nint LaunchBudget;

    /// <summary>
    /// One past the segment (<see cref="ListSegments"/>) whose recording made <see cref="LaunchBudget"/> writable, 0 when this
    /// recording has not zeroed it yet: a new segment runs as a list of its own, after which the buffer is common again.
    /// </summary>
    public int LaunchSegment;

    public static unsafe ListIndirectScratch* Of(XboxObject* list)
    {
        return (ListIndirectScratch*)((byte*)list + Offset);
    }
}
