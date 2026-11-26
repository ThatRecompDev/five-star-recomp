// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// Why an ExecuteIndirect was clamped alone instead of gathered (<see cref="IndirectGather"/>). The "after" reasons name
/// the first work of the segment that may have written memory, whose following barrier or cache flush ended gathering.
/// </summary>
internal enum IndirectGatherMiss
{
    /// <summary>Not a miss; a segment still open, or one whose writer is not known.</summary>
    None,

    /// <summary>A barrier after a Dispatch.</summary>
    AfterDispatch,

    /// <summary>A barrier after an indirect dispatch.</summary>
    AfterIndirectDispatch,

    /// <summary>A barrier after a draw (direct or indirect) of a pipeline whose programs store.</summary>
    AfterStoringDraw,

    /// <summary>A barrier after a draw of a pipeline not decoded (counted as storing).</summary>
    AfterUndecodedDraw,

    /// <summary>A barrier after a buffer or texture copy.</summary>
    AfterCopy,

    /// <summary>A barrier after a memory write (bottom-of-pipe write, WriteBufferImmediate).</summary>
    AfterMemoryWrite,

    /// <summary>A memory wait the list was not split at.</summary>
    MemoryWait,

    /// <summary>The ExecuteIndirect has a count buffer.</summary>
    CountBuffer,

    /// <summary>More records than <see cref="IndirectGather.MaxRecords"/>.</summary>
    TooManyRecords,

    /// <summary>No argument buffer, a signature the clamp does not take, or an offset past 4 GB.</summary>
    NotClampable,

    /// <summary>The gather program is not built.</summary>
    ProgramNotReady,

    /// <summary>The gather buffer could not grow.</summary>
    NoBuffer,

    /// <summary>The segment never opened (one started without a split the gather saw).</summary>
    OtherSegment,
}
