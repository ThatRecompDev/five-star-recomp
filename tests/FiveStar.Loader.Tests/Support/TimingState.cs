// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using Xunit;

namespace FiveStar.Loader.Tests.Support;

/// <summary>
/// Tests of process-wide timing state (<c>GpuTimings</c>'s query heap and sums, <c>FrameLog</c>'s counters) run alone,
/// after the parallel ones: every Xbox device a test creates attaches to the GPU timings while they are on, every draw
/// or dispatch a test records reaches <c>GpuTimings.Work</c>, and every submission adds to the frame log's sums. A full
/// run once reported a timed list with no passes ("queue #1 busy 0.00 ms") this way.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingState
{
    public const string Name = "process-wide timing state";
}
