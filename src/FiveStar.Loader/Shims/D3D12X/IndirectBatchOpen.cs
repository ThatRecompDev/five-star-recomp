// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>Why an ExecuteIndirect opened a new clamp batch (<see cref="IndirectBatch"/>) instead of joining the open one.</summary>
internal enum IndirectBatchOpen
{
    /// <summary>No batch was open: the first ExecuteIndirect after a barrier that followed work that may write, a memory wait, or a new segment.</summary>
    Stretch,

    /// <summary>The job table had no room for its records.</summary>
    TableFull,

    /// <summary>The batch's scratch piece had no room for its records.</summary>
    PieceFull,

    /// <summary>Its argument or count buffer is none of the batch's source buffers.</summary>
    Source,

    /// <summary>Its arguments (or count) may overlap a memory write recorded since the batch opened.</summary>
    Write,
}
