// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// The costly Aftermath features, each opt-in: event markers and call stacks (<c>--aftermath-markers</c>, CPU per
/// draw), resource tracking (<c>--aftermath-resources</c>, names the resource at a page fault; driver memory per
/// resource) and shader debug info (<c>--aftermath-shader-info</c>, line tables for every pipeline, cached until a
/// crash: boot w181 reached 45 GB working set with it, against about 28 GB without).
/// </summary>
internal readonly record struct AftermathFeatures(bool Markers, bool Resources, bool ShaderInfo);
