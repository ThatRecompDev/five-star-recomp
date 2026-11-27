// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Graphics.Replay;

/// <summary>The thread-group counts of one dispatch (D3D12_DISPATCH_ARGUMENTS).</summary>
public sealed record CapturedDispatch(uint X, uint Y, uint Z);
