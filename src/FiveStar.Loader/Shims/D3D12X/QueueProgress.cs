// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// Names the GPU work that stops a queue (docs/d3d12x-shim.md#queue-progress). Every queue keeps its last
/// <see cref="History"/> calls (executions with what their lists recorded, Signals and Waits on game fences); a watch
/// thread reads each queue's shim fence (<see cref="ListSubmissions"/>) four times a second. A queue whose submitted
/// work has not advanced for <see cref="StallSeconds"/> is logged once per stall with its calls since the last
/// finished submission, and when the device is removed (every fence then reads UINT64_MAX) the last values seen before
/// are logged for every queue.
/// </summary>
internal static unsafe class QueueProgress
{
    /// <summary>Queues followed; the game makes a handful.</summary>
    private const int MaxQueues = 32;

    /// <summary>
    /// Calls kept per queue: enough that the oldest unfinished execution is still there in a hang (the graphics queue
    /// had 60 executions and their Signals in flight when the w3 boot lost the device, past the 64 kept before).
    /// </summary>
    private const int History = 1024;

    /// <summary>Calls printed per queue in a report.</summary>
    private const int Printed = 16;

    private const double StallSeconds = 1.5;
    private const int PollMilliseconds = 250;

    /// <summary>ID3D12Fence::GetCompletedValue (d3d12.h).</summary>
    private const int GetCompletedValueSlot = 8;

    private static readonly Queue?[] Queues = new Queue?[MaxQueues];
    private static readonly ConcurrentDictionary<nint, string> PipelineNames = new();
    private static readonly object Gate = new();
    private static LoaderLog? _log;
    private static int _count;
    private static int _removed;

    /// <summary>Starts the watch thread; the queues register themselves on their first call.</summary>
    public static void Start(LoaderLog log)
    {
        _log = log;
        var thread = new Thread(Watch) { IsBackground = true, Name = "fivestar queue progress" };
        thread.Start();
    }

    /// <summary>Names a desktop pipeline for the reports (the translated programs it was built from).</summary>
    public static void Name(nint pipeline, string names)
    {
        if (pipeline != 0)
        {
            PipelineNames[pipeline] = names;
        }
    }

    /// <summary>An execution on <paramref name="queue"/> that ends when its shim fence reaches <paramref name="value"/>.</summary>
    [HotPath]
    public static void Executed(XboxQueueObject* queue, nint* lists, uint count, ulong value, XboxObjects objects)
    {
        var entry = new QueueEvent { At = Stopwatch.GetTimestamp(), Kind = QueueEventKind.Execute, Lists = count, Value = value };

        for (uint i = 0; i < count; i++)
        {
            nint list = lists[i];

            if (list == 0 || objects.Unwrap(list) == list)
            {
                continue;
            }

            var activity = ListActivity.Of((XboxObject*)list);
            entry.Draws += activity->Draws;
            entry.Dispatches += activity->Dispatches;
            entry.Indirect += activity->Indirect;

            if (activity->Pipeline != 0)
            {
                entry.Pipeline = activity->Pipeline;
            }
        }

        Add(queue, entry);
    }

    /// <summary>A Signal or Wait of a game fence on <paramref name="queue"/>.</summary>
    [HotPath]
    public static void FenceCall(XboxObject* queue, QueueEventKind kind, nint fence, ulong value)
    {
        Add((XboxQueueObject*)queue, new QueueEvent { At = Stopwatch.GetTimestamp(), Kind = kind, Fence = fence, Value = value });
    }

    /// <summary>Logs every queue with the values seen before the removal; once.</summary>
    public static void ReportRemoval()
    {
        if (Interlocked.Exchange(ref _removed, 1) != 0)
        {
            return;
        }

        Report("device removed");
    }

    /// <summary>Logs every queue as last seen, after <paramref name="reason"/>.</summary>
    public static void Report(string reason)
    {
        if (_log is null)
        {
            return;
        }

        _log.Error($"[gpu] {reason} ({MemorySnapshot.Summary()}); the queues as last seen:");

        for (int i = 0; i < Volatile.Read(ref _count); i++)
        {
            var queue = Queues[i];

            if (queue is not null)
            {
                _log.Error(Describe(queue, Stopwatch.GetTimestamp()));
            }
        }
    }

    [HotPath]
    private static void Add(XboxQueueObject* queue, QueueEvent entry)
    {
        int number = Volatile.Read(ref queue->Number);

        if (number == 0)
        {
            number = Register(queue);
        }

        if (number <= 0)
        {
            return;
        }

        var history = Queues[number - 1]!;
        long slot = Interlocked.Increment(ref history.Next) - 1;
        history.Events[slot % History] = entry;
    }

    private static int Register(XboxQueueObject* queue)
    {
        lock (Gate)
        {
            if (queue->Number != 0)
            {
                return queue->Number;
            }

            if (_count == MaxQueues)
            {
                queue->Number = -1;

                return -1;
            }

            Queues[_count] = new Queue((nint)queue, _count + 1);
            Volatile.Write(ref _count, _count + 1);
            queue->Number = _count;

            return _count;
        }
    }

    private static void Watch()
    {
        while (Volatile.Read(ref _removed) == 0)
        {
            Thread.Sleep(PollMilliseconds);
            long now = Stopwatch.GetTimestamp();

            for (int i = 0; i < Volatile.Read(ref _count); i++)
            {
                var queue = Queues[i];

                if (queue is not null)
                {
                    Poll(queue, now);
                }
            }
        }
    }

    private static void Poll(Queue queue, long now)
    {
        var wrapper = (XboxQueueObject*)queue.Wrapper;
        nint fence = Volatile.Read(ref wrapper->Fence);

        if (fence == 0)
        {
            return;
        }

        ulong completed = Completed(fence);

        if (completed == ulong.MaxValue)
        {
            ReportRemoval();

            return;
        }

        if (completed != queue.Seen || queue.SeenAt == 0)
        {
            queue.Seen = completed;
            queue.SeenAt = now;
            queue.Stalled = false;

            return;
        }

        bool behind = Volatile.Read(ref wrapper->Signaled) > completed;

        if (behind && !queue.Stalled && Stopwatch.GetElapsedTime(queue.SeenAt, now).TotalSeconds > StallSeconds)
        {
            queue.Stalled = true;
            _log?.Warn($"[gpu] {Describe(queue, now)}\n      {MemorySnapshot.Summary()}");
        }
    }

    private static ulong Completed(nint fence)
    {
        return ((delegate* unmanaged<nint, ulong>)ComObject.Slot(fence, GetCompletedValueSlot))(fence);
    }

    /// <summary>The queue's progress and its calls since its last finished execution (at least the last few).</summary>
    private static string Describe(Queue queue, long now)
    {
        var wrapper = (XboxQueueObject*)queue.Wrapper;
        var text = new StringBuilder();
        double quiet = queue.SeenAt == 0 ? 0 : Stopwatch.GetElapsedTime(queue.SeenAt, now).TotalSeconds;
        text.Append(CultureInfo.InvariantCulture, $"queue #{queue.Number}: executions done up to {queue.Seen} of {Volatile.Read(ref wrapper->Signaled)}, no progress for {quiet:F1} s");

        long next = Volatile.Read(ref queue.Next);
        long first = Math.Max(0, next - History);
        long start = Math.Max(first, next - Printed);
        long oldest = OldestUnfinished(queue, first, next);

        // The oldest unfinished execution is the one the queue waits on: named even when it is before the window.
        if (oldest >= 0 && oldest < start)
        {
            var entry = queue.Events[oldest % History];
            text.Append("\n      oldest unfinished: ").Append(Format(entry, queue.Seen, now));
            GpuBreadcrumbs.Describe(text, Volatile.Read(ref wrapper->Fence), entry.Value, PipelineName);
            text.Append(CultureInfo.InvariantCulture, $"\n      ({start - oldest - 1} calls after it not shown)");
        }

        for (long i = start; i < next; i++)
        {
            var entry = queue.Events[i % History];
            text.Append("\n      ").Append(Format(entry, queue.Seen, now));

            // The last finished execution as well: its lists show the markers reached the CPU.
            if (entry.Kind == QueueEventKind.Execute && entry.Value >= queue.Seen)
            {
                GpuBreadcrumbs.Describe(text, Volatile.Read(ref wrapper->Fence), entry.Value, PipelineName);
            }
        }

        return text.ToString();
    }

    /// <summary>The position of the first execution after the last finished one in the kept calls; -1 for none.</summary>
    private static long OldestUnfinished(Queue queue, long first, long next)
    {
        long oldest = -1;

        for (long i = next - 1; i >= first; i--)
        {
            var entry = queue.Events[i % History];

            if (entry.Kind != QueueEventKind.Execute)
            {
                continue;
            }

            if (entry.Value <= queue.Seen)
            {
                break;
            }

            oldest = i;
        }

        return oldest;
    }

    private static string Format(QueueEvent entry, ulong seen, long now)
    {
        double ago = Stopwatch.GetElapsedTime(entry.At, now).TotalSeconds;

        switch (entry.Kind)
        {
            case QueueEventKind.Execute:
                string state = entry.Value <= seen ? "done" : "NOT DONE";
                string pipeline = PipelineName(entry.Pipeline);

                return string.Create(CultureInfo.InvariantCulture, $"{ago:F2} s ago execute #{entry.Value} {state}: {entry.Lists} lists, {entry.Draws} draws, {entry.Dispatches} dispatches, {entry.Indirect} indirect; last pipeline {pipeline}");
            default:
                return string.Create(CultureInfo.InvariantCulture, $"{ago:F2} s ago {entry.Kind} fence 0x{entry.Fence:x} to {entry.Value} (fence at {FenceValue(entry.Fence)})");
        }
    }

    /// <summary>A desktop pipeline by the programs it was built from, or its address.</summary>
    public static string PipelineName(nint pipeline)
    {
        if (pipeline == 0)
        {
            return "none";
        }

        if (Enum.IsDefined((RecordedCommand)pipeline))
        {
            return GpuBreadcrumbs.CommandName(pipeline);
        }

        return PipelineNames.GetValueOrDefault(pipeline, $"0x{pipeline:x}");
    }

    /// <summary>The completed value of a game fence (a wrapper or a desktop fence), as text.</summary>
    private static string FenceValue(nint fence)
    {
        var objects = ShimContext.Runtime.D3D12X.Objects;
        nint inner = objects.Unwrap(fence);

        if (inner == 0)
        {
            return "?";
        }

        return Completed(inner).ToString(CultureInfo.InvariantCulture);
    }

    private sealed class Queue(nint wrapper, int number)
    {
        public long Next;
        public ulong Seen;
        public long SeenAt;
        public bool Stalled;

        public nint Wrapper { get; } = wrapper;

        public int Number { get; } = number;

        public QueueEvent[] Events { get; } = new QueueEvent[History];
    }
}
