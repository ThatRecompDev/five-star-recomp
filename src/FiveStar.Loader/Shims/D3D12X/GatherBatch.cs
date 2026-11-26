// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>A command list's gather buffer (<see cref="IndirectGather"/>) and the records its recording gathered.</summary>
internal sealed class GatherBatch
{
    public nint Buffer { get; set; }

    public ulong Bytes { get; set; }

    public ulong Used { get; set; }

    public List<GatherRecord> Records { get; } = [];
}
