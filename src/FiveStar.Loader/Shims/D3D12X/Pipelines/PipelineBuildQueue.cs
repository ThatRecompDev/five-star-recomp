// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// A fixed set of background threads that run desktop pipeline creations in order, so creations the game no longer
/// waits for (<see cref="StandInPipelines"/>) never take more driver compiler threads than these. A thread starts a
/// creation only while the machine has the memory for it: the driver's compiles of a story load's backlog took a
/// 64 GB machine to its commit limit, which ended the process (docs/d3d12x-pipelines.md#stand-ins). Urgent creations,
/// those of pipelines a command list has set, start before all others; a queued creation can be made urgent later.
/// Only half the threads take the others, and those wait in the driver compile budget while an urgent one does: at a
/// warm boot the journal's creations held the budget the game's first uses needed.
/// </summary>
internal sealed class PipelineBuildQueue
{
    private const int PauseMilliseconds = 2000;

    // One thread in this many also takes creations no command list waits for.
    private const int SpeculativeShare = 2;

    /// <summary>For <see cref="PipelineBuildQueue(int, Func{int, bool})"/>: creations start whenever a thread is free.</summary>
    public static readonly Func<int, bool> AnyTime = _ => true;

    private readonly BlockingCollection<PipelineBuild> _urgent = [];
    private readonly BlockingCollection<PipelineBuild> _builds = [];
    private readonly Func<int, bool> _room;
    private int _waiting;
    private int _running;

    /// <param name="workers">Threads that call the driver; at least one.</param>
    /// <param name="room">Whether a creation may start now, given the creations running; polled until it says so.</param>
    public PipelineBuildQueue(int workers, Func<int, bool> room)
    {
        Workers = Math.Max(1, workers);
        _room = room;
    }

    /// <summary>The number of threads that run creations.</summary>
    public int Workers { get; }

    /// <summary>Creations queued and not yet started.</summary>
    public int Backlog => _urgent.Count + _builds.Count;

    /// <summary>Threads waiting for memory before their next creation.</summary>
    public int WaitingForMemory => Volatile.Read(ref _waiting);

    /// <summary>Starts the threads; call once.</summary>
    public void Start()
    {
        BlockingCollection<PipelineBuild>[] both = [_urgent, _builds];
        BlockingCollection<PipelineBuild>[] urgentOnly = [_urgent];

        for (int i = 0; i < Workers; i++)
        {
            var queues = urgentOnly;

            if (i < SpeculativeWorkers)
            {
                queues = both;
            }

            var thread = new Thread(() => Run(queues)) { IsBackground = true, Name = "fivestar pipeline build" };
            thread.Start();
        }
    }

    /// <summary>
    /// The threads that also take creations no command list waits for yet (the journal's at a warm boot), half of them
    /// (at least one); the others are kept for urgent ones.
    /// </summary>
    public int SpeculativeWorkers => Math.Max(1, Workers / SpeculativeShare);

    /// <summary>Queues <paramref name="create"/>; the task ends with its result.</summary>
    public Task<PipelineResult> Enqueue(Func<PipelineResult> create)
    {
        return Queue(create, urgent: false).Done.Task;
    }

    /// <summary>Queues <paramref name="create"/>, before every creation that is not urgent when <paramref name="urgent"/>.</summary>
    public PipelineBuild Queue(Func<PipelineResult> create, bool urgent)
    {
        var build = new PipelineBuild(create, urgent);

        if (urgent)
        {
            _urgent.Add(build);
        }
        else
        {
            _builds.Add(build);
        }

        return build;
    }

    /// <summary>Moves a queued <paramref name="build"/> before every creation that is not urgent.</summary>
    public void Promote(PipelineBuild build)
    {
        if (build.MakeUrgent())
        {
            _urgent.Add(build);
        }
    }

    private void Run(BlockingCollection<PipelineBuild>[] queues)
    {
        while (true)
        {
            // Memory first, then the work: a thread waiting for memory holds no creation, so an urgent one queued
            // meanwhile is the next taken. TakeFromAny takes from the first queue that has work.
            WaitForRoom();
            BlockingCollection<PipelineBuild>.TakeFromAny(queues, out var build);

            if (build!.Claim())
            {
                Execute(build, build.Urgent);
            }
        }
    }

    /// <summary>Runs a queued <paramref name="build"/> on this thread (a game thread that waits for it) unless a build thread took it; true when this ran it.</summary>
    public bool RunNow(PipelineBuild build)
    {
        if (!build.Claim())
        {
            return false;
        }

        Execute(build, urgent: true);

        return true;
    }

    /// <summary>Runs a build; an urgent one goes before the others in the driver compile budget (<see cref="CompileBudget"/>).</summary>
    private void Execute(PipelineBuild build, bool urgent)
    {
        Interlocked.Increment(ref _running);
        var previous = CompileBudget.Urgent;
        CompileBudget.Urgent = urgent;

        try
        {
            build.Done.SetResult(build.Create());
        }
        finally
        {
            CompileBudget.Urgent = previous;
            Interlocked.Decrement(ref _running);
        }
    }

    private void WaitForRoom()
    {
        if (_room(Volatile.Read(ref _running)))
        {
            return;
        }

        Interlocked.Increment(ref _waiting);

        while (!_room(Volatile.Read(ref _running)))
        {
            Thread.Sleep(PauseMilliseconds);
        }

        Interlocked.Decrement(ref _waiting);
    }
}
