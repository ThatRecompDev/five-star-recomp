// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Graphics.Replay;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>A descriptor of a capture: at desktop index <see cref="Index"/>, a view of source <see cref="Resource"/> (null: a null view) with its desc bytes.</summary>
internal sealed record PlannedView(uint Index, CapturedViewKind Kind, string? Resource, byte[] Description);
