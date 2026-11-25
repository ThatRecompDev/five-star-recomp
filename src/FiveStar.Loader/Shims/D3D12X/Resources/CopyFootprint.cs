// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>D3D12_PLACED_SUBRESOURCE_FOOTPRINT (d3d12.h): one subresource laid out in a buffer for a texture copy.</summary>
internal readonly record struct CopyFootprint(ulong Offset, int Format, uint Width, uint Height, uint Depth, uint RowPitch);
