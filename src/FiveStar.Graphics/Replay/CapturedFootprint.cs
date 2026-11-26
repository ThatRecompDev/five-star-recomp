// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Graphics.Replay;

/// <summary>
/// Where one subresource of a texture sits in its blobs (D3D12_PLACED_SUBRESOURCE_FOOTPRINT, as GetCopyableFootprints
/// gives it) and the bytes of each of its <see cref="Rows"/> rows that hold texels.
/// </summary>
public sealed record CapturedFootprint(ulong Offset, int Format, uint Width, uint Height, uint Depth, uint RowPitch, uint Rows, ulong RowBytes);
