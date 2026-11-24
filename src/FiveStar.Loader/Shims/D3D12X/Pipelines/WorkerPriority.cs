// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// The thread priority of one pipeline build while a build thread runs it. A creation a command list has set or a game
/// thread waits for runs at normal priority; speculative ones (the journal's, and creations the game stopped waiting
/// for) run below normal, so the driver's compiles take the processors the game leaves idle instead of the game's own
/// (docs/d3d12x-pipelines.md#thread-priority). A creation that becomes urgent while it runs has its thread raised.
/// Only build threads change priority, never a game thread that runs a creation itself. Program compiles keep the
/// asking thread's priority in the compile host. <c>--normal-priority-builds</c> keeps every build at normal priority.
/// </summary>
internal sealed class WorkerPriority
{
    private Thread? _runner;

    /// <summary>False with <c>--normal-priority-builds</c>.</summary>
    public static bool Lowered { get; set; } = true;

    /// <summary>On a build thread, before it runs the work: sets the thread's priority for the work.</summary>
    public void Begin(bool urgent)
    {
        var thread = Thread.CurrentThread;
        thread.Priority = urgent || !Lowered ? ThreadPriority.Normal : ThreadPriority.BelowNormal;
        Volatile.Write(ref _runner, thread);
    }

    /// <summary>On the same thread, after the work.</summary>
    public void End()
    {
        Interlocked.CompareExchange(ref _runner, null, Thread.CurrentThread);
    }

    /// <summary>
    /// The work became urgent: the thread running it, if any, goes to normal priority. A thread that moved on to other
    /// work meanwhile keeps normal priority only until it begins its next work.
    /// </summary>
    public void Raise()
    {
        var runner = Volatile.Read(ref _runner);

        if (runner is not null)
        {
            runner.Priority = ThreadPriority.Normal;
        }
    }
}
