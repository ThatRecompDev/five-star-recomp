// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// The background threads that translate and compile programs on demand (<see cref="OnDemandPrograms"/>), off the
/// game's threads. Work a command list waits for goes before prefetched work; compiles of huge sources run on a thread
/// of their own, so one compile that takes minutes holds up nothing else. Like pipeline builds, work starts only while
/// the machine has the memory for it (<see cref="WarmMemoryGuard"/>). The threads run below normal priority, as do the
/// pipeline builds: the game's threads, and the programs they make inline, come first (a Subrender worker that waited
/// 8 s for a small inline compile among 30 busy compile threads stalled a frame for 22 s).
/// </summary>
internal sealed class ProgramWorkers
{
    private const int PauseMilliseconds = 2000;

    private readonly BlockingCollection<Action> _urgent = [];
    private readonly BlockingCollection<Action> _prefetch = [];
    private readonly BlockingCollection<Action> _heavy = [];
    private readonly Func<int, bool> _room;
    private readonly Func<int, bool> _heavyRoom;
    private int _running;
    private int _waiting;

    /// <param name="workers">Threads for ordinary work; at least one. One more thread runs heavy work.</param>
    /// <param name="room">Whether ordinary work may start, given the work running.</param>
    /// <param name="heavyRoom">Whether heavy work may start, given the work running.</param>
    public ProgramWorkers(int workers, Func<int, bool> room, Func<int, bool> heavyRoom)
    {
        Workers = Math.Max(1, workers);
        _room = room;
        _heavyRoom = heavyRoom;
    }

    /// <summary>The threads for ordinary work.</summary>
    public int Workers { get; }

    /// <summary>Work queued and not started.</summary>
    public int Backlog => _urgent.Count + _prefetch.Count + _heavy.Count;

    /// <summary>Work running now.</summary>
    public int Running => Volatile.Read(ref _running);

    /// <summary>Threads waiting for memory before their next work.</summary>
    public int WaitingForMemory => Volatile.Read(ref _waiting);

    /// <summary>Starts the threads; call once.</summary>
    public void Start()
    {
        for (int i = 0; i < Workers; i++)
        {
            new Thread(RunOrdinary) { IsBackground = true, Name = "fivestar program compile", Priority = ThreadPriority.BelowNormal }.Start();
        }

        new Thread(RunHeavy) { IsBackground = true, Name = "fivestar heavy program compile", Priority = ThreadPriority.BelowNormal }.Start();
    }

    /// <summary>Queues ordinary work; <paramref name="urgent"/> for work a command list waits for.</summary>
    public void Queue(Action work, bool urgent)
    {
        if (urgent)
        {
            _urgent.Add(work);
        }
        else
        {
            _prefetch.Add(work);
        }
    }

    /// <summary>Queues work for the heavy thread.</summary>
    public void QueueHeavy(Action work)
    {
        _heavy.Add(work);
    }

    private void RunOrdinary()
    {
        BlockingCollection<Action>[] queues = [_urgent, _prefetch];

        while (true)
        {
            // Memory first, then the work: a thread waiting for memory holds no work, so urgent work queued meanwhile
            // is the next taken. TakeFromAny takes from the first queue that has work.
            WaitForRoom(_room);
            BlockingCollection<Action>.TakeFromAny(queues, out var work);
            Run(work!);
        }
    }

    private void RunHeavy()
    {
        while (true)
        {
            WaitForRoom(_heavyRoom);
            Run(_heavy.Take());
        }
    }

    private void WaitForRoom(Func<int, bool> room)
    {
        if (room(Running))
        {
            return;
        }

        Interlocked.Increment(ref _waiting);

        while (!room(Running))
        {
            Thread.Sleep(PauseMilliseconds);
        }

        Interlocked.Decrement(ref _waiting);
    }

    private void Run(Action work)
    {
        Interlocked.Increment(ref _running);

        try
        {
            work();
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}
