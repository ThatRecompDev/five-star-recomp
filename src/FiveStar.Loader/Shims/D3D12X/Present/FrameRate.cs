// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Present;

/// <summary>
/// <c>--frame-rate</c>: the frame interval the frame events keep (<see cref="FramePacer"/>). Console keeps the game's own
/// choice (33333 µs, 30 fps at 60 Hz); sixty caps it at one 60 Hz refresh; uncapped makes a 1 ms grid and presents
/// without vertical sync.
/// </summary>
internal enum FrameRate
{
    Console,
    Sixty,
    Uncapped,
}
