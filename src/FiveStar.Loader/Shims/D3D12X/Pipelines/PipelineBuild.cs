// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// One queued desktop pipeline creation (<see cref="PipelineBuildQueue"/>) and where its result goes. A build promoted
/// to urgent sits in both queues; the first thread to claim it runs it. A build thread runs it at normal priority when
/// it is urgent or a game thread still waits for it, else below normal (<see cref="WorkerPriority"/>).
/// </summary>
internal sealed class PipelineBuild
{
    private int _claimed;
    private int _urgent;
    private int _awaited;

    public PipelineBuild(Func<PipelineResult> create, bool urgent, bool awaited)
    {
        Create = create;
        _urgent = urgent ? 1 : 0;
        _awaited = awaited ? 1 : 0;
    }

    public Func<PipelineResult> Create { get; }

    /// <summary>Whether a command list waits for it (queued urgent or promoted since).</summary>
    public bool Urgent => Volatile.Read(ref _urgent) == 1;

    /// <summary>The priority of the build thread while it runs this build.</summary>
    public WorkerPriority Priority { get; } = new();

    /// <summary>Whether a build thread taking it now runs it at normal priority: urgent, or a game thread waits for it.</summary>
    public bool Pressing => Urgent || Volatile.Read(ref _awaited) == 1;

    public TaskCompletionSource<PipelineResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>True for the one caller that runs the build.</summary>
    public bool Claim()
    {
        return Interlocked.Exchange(ref _claimed, 1) == 0;
    }

    /// <summary>The game thread that waited for the build gave up (it got a stand-in).</summary>
    public void StopAwaiting()
    {
        Volatile.Write(ref _awaited, 0);
    }

    /// <summary>True for the first call on a build that was not urgent yet: it then needs an urgent queue entry.</summary>
    public bool MakeUrgent()
    {
        return Interlocked.Exchange(ref _urgent, 1) == 0 && Volatile.Read(ref _claimed) == 0;
    }
}
