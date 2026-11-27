// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Graphics.Replay;

/// <summary>
/// <see cref="Count"/> dwords, one every <see cref="Stride"/> bytes from <see cref="Offset"/>: <see cref="Value"/>, plus
/// <see cref="Step"/> more for each one after the first (the window's null view records, its sampler records naming
/// their own index).
/// </summary>
public sealed record CapturedFill(ulong Offset, uint Stride, uint Count, uint Value)
{
    public uint Step { get; init; }
}
