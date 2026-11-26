// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>One <c>--capture-dispatch</c> entry: the <see cref="Occurrence"/>-th dispatch (from 1) of a compute pipeline whose name holds <see cref="Text"/>.</summary>
internal sealed record CaptureSpec(string Text, int Occurrence);
