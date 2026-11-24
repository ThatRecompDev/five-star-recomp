// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.Pipelines;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// Build threads run creations nothing waits for below normal priority, so the game's threads go first; creations a
/// command list or a game thread waits for, and creations promoted while they run, run at normal priority.
/// </summary>
public sealed class WorkerPriorityTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    [Fact]
    public void Speculative_creations_run_below_normal_and_awaited_ones_at_normal()
    {
        var queue = new PipelineBuildQueue(1, PipelineBuildQueue.AnyTime);
        queue.Start();

        Assert.Equal(ThreadPriority.BelowNormal, PriorityOf(queue, urgent: false, awaited: false));
        Assert.Equal(ThreadPriority.Normal, PriorityOf(queue, urgent: true, awaited: false));
        Assert.Equal(ThreadPriority.Normal, PriorityOf(queue, urgent: false, awaited: true));
    }

    [Fact]
    public async Task A_creation_promoted_while_it_runs_raises_its_thread()
    {
        using var running = new ManualResetEventSlim();
        using var promoted = new ManualResetEventSlim();
        var queue = new PipelineBuildQueue(1, PipelineBuildQueue.AnyTime);
        queue.Start();
        ThreadPriority before = default, after = default;

        var build = queue.Queue(() =>
        {
            before = Thread.CurrentThread.Priority;
            running.Set();
            promoted.Wait(Limit);
            after = Thread.CurrentThread.Priority;

            return default;
        }, urgent: false);

        Assert.True(running.Wait(Limit));
        queue.Promote(build);
        promoted.Set();

        await build.Done.Task.WaitAsync(Limit);
        Assert.Equal(ThreadPriority.BelowNormal, before);
        Assert.Equal(ThreadPriority.Normal, after);
    }

    [Fact]
    public void A_creation_the_game_thread_runs_itself_keeps_that_threads_priority()
    {
        var room = false;
        var queue = new PipelineBuildQueue(1, _ => Volatile.Read(ref room));
        queue.Start();
        ThreadPriority seen = default;

        var thread = new Thread(() =>
        {
            var build = queue.Queue(() =>
            {
                seen = Thread.CurrentThread.Priority;

                return default;
            }, urgent: false);

            queue.RunNow(build);
        }) { Priority = ThreadPriority.AboveNormal };

        thread.Start();

        Assert.True(thread.Join(Limit));
        Assert.Equal(ThreadPriority.AboveNormal, seen);
    }

    [Fact]
    public void Normal_priority_builds_keep_every_creation_at_normal()
    {
        var queue = new PipelineBuildQueue(1, PipelineBuildQueue.AnyTime);
        queue.Start();
        WorkerPriority.Lowered = false;

        try
        {
            Assert.Equal(ThreadPriority.Normal, PriorityOf(queue, urgent: false, awaited: false));
        }
        finally
        {
            WorkerPriority.Lowered = true;
        }
    }

    private static ThreadPriority PriorityOf(PipelineBuildQueue queue, bool urgent, bool awaited)
    {
        ThreadPriority seen = default;

        var build = queue.Queue(() =>
        {
            seen = Thread.CurrentThread.Priority;

            return default;
        }, urgent, awaited);

        Assert.True(SpinWait.SpinUntil(() => build.Done.Task.IsCompleted, Limit));

        return seen;
    }
}
