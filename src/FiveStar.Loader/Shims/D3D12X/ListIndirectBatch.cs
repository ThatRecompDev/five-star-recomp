// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// A command list's open clamp batch (<see cref="IndirectBatch"/>), kept in its wrapper: the scratch piece its records are
/// clamped into, its job table in upload memory, the source buffers its pass reads and the memory writes recorded since it
/// opened. The source buffers stay after the batch ends, as the first guess of the next one.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ListIndirectBatch
{
    /// <summary>Offset in the command-list wrapper, past <see cref="ListIndirectScratch"/> (0x1A00 to about 0x1AE0).</summary>
    public const int Offset = 0x1B00;

    /// <summary>Source buffers one pass reads (root views t0 to t3).</summary>
    public const int Sources = 4;

    /// <summary>Memory writes of known ranges kept apart while a batch is open, at most this many.</summary>
    public const int MaxWrites = 8;

    /// <summary>1 while ExecuteIndirects may join the batch.</summary>
    public int Open;

    /// <summary>Jobs (records) in the table so far.</summary>
    public int Jobs;

    /// <summary>The source slot the next new source buffer takes.</summary>
    public int NextSource;

    /// <summary>The memory writes recorded since the batch opened: <see cref="WriteCount"/> console ranges.</summary>
    public int WriteCount;

    /// <summary>The scratch buffer (<see cref="ListIndirectScratch.Buffer"/>) the batch's piece is in, and its size.</summary>
    public nint Buffer;
    public ulong BufferBytes;

    /// <summary>The batch's piece in <see cref="Buffer"/>: its offset and the bytes records took of it.</summary>
    public ulong Piece;
    public ulong PieceUsed;

    /// <summary>The job table: its CPU address in the upload ring (written only, the ring is write-combined).</summary>
    public nint Table;

    /// <summary>The desktop buffers the pass reads, by slot (0: none).</summary>
    public fixed long SourceBuffers[Sources];

    public fixed ulong WriteStarts[MaxWrites];
    public fixed ulong WriteEnds[MaxWrites];

    public static ListIndirectBatch* Of(XboxObject* list)
    {
        return (ListIndirectBatch*)((byte*)list + Offset);
    }
}
