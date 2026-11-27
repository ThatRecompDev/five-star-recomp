// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Graphics.Replay;

/// <summary>How a replay's output differs from the reference: differing dwords, the byte offsets of the first ones, the bytes compared.</summary>
public sealed record OutputDifference(string Resource, long Differing, IReadOnlyList<ulong> FirstOffsets, ulong Compared)
{
    public bool Same => Differing == 0;
}
