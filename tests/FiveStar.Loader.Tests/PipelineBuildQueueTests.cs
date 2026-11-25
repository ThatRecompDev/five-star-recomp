// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Pipelines;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>A creation a game thread runs at first use: one the compile budget would make it wait for goes back to the build threads.</summary>
public sealed class PipelineBuildQueueTests
{
    private static readonly TimeSpan BuildLimit = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_creation_that_may_not_start_on_a_game_thread_goes_back_to_the_build_threads()
    {
        var queue = new PipelineBuildQueue(1, PipelineBuildQueue.AnyTime);
        bool? noWaitSeen = null;
        var build = queue.Queue(() =>
        {
            noWaitSeen ??= CompileBudget.NoWait;

            // On the game thread the budget has no room; on a build thread it builds.
            if (CompileBudget.NoWait)
            {
                return new PipelineResult(HResult.Pending, 0);
            }

            return new PipelineResult(HResult.Ok, 1);
        }, urgent: false);

        Assert.False(queue.RunNow(build));
        Assert.True(noWaitSeen);
        Assert.False(CompileBudget.NoWait);
        Assert.False(build.Done.Task.IsCompleted);

        queue.Start();

        var result = await build.Done.Task.WaitAsync(BuildLimit);
        Assert.Equal(HResult.Ok, result.Hr);
    }
}
