// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Present;

/// <summary>
/// <c>--frame-rate</c>: the frame interval the frame events keep (<see cref="FramePacer"/>). Console keeps the game's own
/// choice (33333 µs, 30 fps at 60 Hz); sixty caps it at one 60 Hz refresh; uncapped makes a 1 ms grid and presents
/// without vertical sync. Only our pacer: the title reads its own 30 fps setting (settings +0x210) at +0x1e0fc62,
/// +0x224359a and in its timer floor +0x2241fdb, so once frames take under 33 ms its timers may step 33 ms each (as
/// VIctory-7900XTX found: game time at 2x until all three reads were patched).
/// </summary>
internal enum FrameRate
{
    Console,
    Sixty,
    Uncapped,
}
