// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>Who asks <see cref="SectionHeaps"/> for a heap, which decides how much of the budget the request may take.</summary>
internal enum SectionHeapUse
{
    /// <summary>A slot-133 buffer the game placed at these pages: the GPU reads them for certain.</summary>
    Placed,

    /// <summary>The address map, for pages the game mapped: programs may read them through a V# or pointer.</summary>
    Map,
}
