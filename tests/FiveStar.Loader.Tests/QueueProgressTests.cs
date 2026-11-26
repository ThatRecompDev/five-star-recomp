// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using FiveStar.Loader.Shims.D3D12X;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>The removal report's GPU memory line, and when a queue counts as stalled.</summary>
public sealed class QueueProgressTests
{
    [Fact]
    public void The_removal_report_carries_the_gpu_memory_line_or_why_it_is_missing()
    {
        try
        {
            QueueProgress.Memory = () => "GPU memory: local 9.80 of 11.20 GiB budget; system 6.10 of 7.80 GiB budget";
            string read = QueueProgress.MemoryLine();
            QueueProgress.Memory = () => throw new InvalidOperationException("adapter gone");
            string failed = QueueProgress.MemoryLine();

            Assert.Equal("; GPU memory: local 9.80 of 11.20 GiB budget; system 6.10 of 7.80 GiB budget", read);
            Assert.Equal("; GPU memory unread: adapter gone", failed);
        }
        finally
        {
            QueueProgress.Memory = null;
        }
    }

    [Fact]
    public void An_idle_queue_is_not_a_stall_but_pending_work_without_progress_is_once()
    {
        var queue = new QueueProgress.Queue(0, 2);
        long second = Stopwatch.Frequency;

        Assert.False(QueueProgress.Observe(queue, 6, 6, 1 * second));
        Assert.False(QueueProgress.Observe(queue, 6, 6, 70 * second));    // idle for 70 s
        Assert.False(QueueProgress.Observe(queue, 6, 12, 70 * second + (second / 10)));    // just submitted
        Assert.True(QueueProgress.Observe(queue, 6, 12, 72 * second));
        Assert.False(QueueProgress.Observe(queue, 6, 12, 80 * second));    // reported once

        Assert.False(QueueProgress.Observe(queue, 7, 12, 81 * second));
        Assert.True(QueueProgress.Observe(queue, 7, 12, 83 * second));
    }
}
