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
internal unsafe struct ListIndirectScratch
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

    /// <summary>1 while the segment <see cref="GatherSegment"/> may still gather records (<see cref="IndirectGather"/>).</summary>
    public int GatherOpen;
    public int GatherSegment;

    /// <summary>1 once work of the segment may have written memory: its next barrier ends gathering.</summary>
    public int GatherWritten;

    /// <summary>The first work of the segment that may have written (an <see cref="IndirectGatherMiss"/>), and its pipeline.</summary>
    public int GatherWriter;
    public nint GatherWriterPipeline;

    /// <summary>Why gathering ended in the segment (an <see cref="IndirectGatherMiss"/>); 0 while it is open.</summary>
    public int GatherClosed;

    /// <summary>Memory writes of the segment with known console ranges kept apart (<see cref="IndirectGather.WroteMemory"/>), at most this many.</summary>
    public const int MaxWrites = 8;

    /// <summary>The segment's memory writes kept apart: console start and end addresses, <see cref="WriteCount"/> of them.</summary>
    public int WriteCount;
    public fixed ulong WriteStarts[MaxWrites];
    public fixed ulong WriteEnds[MaxWrites];

    public static ListIndirectScratch* Of(XboxObject* list)
    {
        return (ListIndirectScratch*)((byte*)list + Offset);
    }
}
