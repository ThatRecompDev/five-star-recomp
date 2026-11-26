// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Present;

/// <summary>One frame's sums of the submit path (<see cref="FrameLog.Take"/>), in Stopwatch ticks: all of it, and its parts.</summary>
internal readonly record struct SubmitTicks(long Submit, long Refresh, long ListWaits, long Preludes);
