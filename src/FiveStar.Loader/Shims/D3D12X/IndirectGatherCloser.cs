// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// The barrier or flush that ended a segment's gathering after work that may have written (<see cref="IndirectGather"/>):
/// whether it made a resource an indirect argument (the dependency gathering guards against) or only something else.
/// </summary>
internal enum IndirectGatherCloser
{
    /// <summary>A ResourceBarrier with a transition into INDIRECT_ARGUMENT.</summary>
    ArgumentTransition,

    /// <summary>A ResourceBarrier with a UAV barrier and no transition into INDIRECT_ARGUMENT.</summary>
    UavBarrier,

    /// <summary>A ResourceBarrier of other transitions or aliasing only.</summary>
    OtherTransition,

    /// <summary>An enhanced Barrier.</summary>
    EnhancedBarrier,

    /// <summary>A GPU cache or pipeline flush.</summary>
    CacheFlush,
}
