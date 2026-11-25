// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Present;

/// <summary>One frame's parts for <see cref="FrameLog.Line"/>, in milliseconds.</summary>
internal readonly record struct FrameTimes(double Frame, double Game, double Submit, double Refresh, double Mirroring, double Collections, double Present, double Latency, double Retire, double CopySubmit, double Swap, double AllocatedMegabytes = 0, int Gen0 = 0, int Gen1 = 0, int Gen2 = 0);
