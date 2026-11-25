// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.Present;
using FiveStar.Loader.Tests.Support;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// <c>--frame-log</c>: our submit path's time is summed per frame and taken once a frame; a line is due every n-th frame
/// and for every slow one, and names each part of the frame. Off, nothing is timed.
/// </summary>
[Collection(TimingState.Name)]
public sealed class FrameLogTests
{
    [Fact]
    public void The_submit_path_is_summed_per_frame_only_when_on()
    {
        try
        {
            FrameLog.Every = 0;
            Assert.Equal(0, FrameLog.Start());

            FrameLog.Every = 10;
            FrameLog.Take();
            long start = FrameLog.Start();
            Thread.Sleep(2);
            FrameLog.AddRefresh(start);
            FrameLog.AddSubmit(start);

            var (submit, refresh) = FrameLog.Take();
            Assert.True(refresh > 0 && submit >= refresh);

            // Taken in every ExecuteCommandLists: no allocation per submission.
            long before = GC.GetAllocatedBytesForCurrentThread();
            FrameLog.AddSubmit(FrameLog.Start());
            FrameLog.AddRefresh(FrameLog.Start());
            FrameLog.Take();
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.Equal((0L, 0L), FrameLog.Take());
        }
        finally
        {
            FrameLog.Every = 0;
        }
    }

    [Fact]
    public void A_line_is_due_every_nth_frame_and_for_a_slow_one_and_names_the_parts()
    {
        try
        {
            FrameLog.Every = 30;

            Assert.True(FrameLog.Logs(60, 40, 250));
            Assert.False(FrameLog.Logs(61, 40, 250));
            Assert.True(FrameLog.Logs(61, 600, 250));

            var times = new FrameTimes(612.4, 600.1, 257.0, 12.5, 4.7, 102.0, 12.3, 0, 0.5, 1.2, 10.6, 48.3);
            string line = FrameLog.Line(61, times);

            Assert.Equal("  [frame] present 61: 612.4 ms = game 600.1 (our submit path 257.0, of it texture refresh 12.5; descriptor mirroring 4.7; GC pauses 102.0 in 0/0/0 collections of gen 0/1/2, 48.3 MB allocated) + present 12.3 (latency wait 0.0, retire wait 0.5, copy submit 1.2, Present 10.6)", line);
        }
        finally
        {
            FrameLog.Every = 0;
        }
    }
}
