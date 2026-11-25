// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;
using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// <c>--gpu-timings</c>: how long the GPU spends in each pass of the game's frames (issue #40). A pass is a run of work
/// in one command list between render-target changes or a switch between draws and dispatches, named by its first
/// pipeline (<see cref="PassName"/>). Each pass begins with a timestamp query and the list's Close ends the last; Close
/// resolves the list's block of <see cref="QueriesPerBlock"/> queries into a readback buffer. Each submission queues
/// the lists' passes with the queue fence value they end at, and a thread reads them once the fence passed (no wait
/// on the GPU): durations are summed per queue and pass, and <see cref="Report"/> prints them per frame with the
/// sample line, the 10 largest per queue and the queue's busy time. One query pair per pass, not per draw.
/// </summary>
internal static unsafe class GpuTimings
{
    /// <summary>Query blocks in the ring, one per recording; at about 300 recordings a frame the ring turns every second or so.</summary>
    private const int Blocks = 8192;

    /// <summary>Queries per recording: up to 31 passes and the end; later boundaries extend the last pass.</summary>
    private const int QueriesPerBlock = 32;

    private const int Queries = Blocks * QueriesPerBlock;

    /// <summary>ID3D12GraphicsCommandList::EndQuery and ::ResolveQueryData; ID3D12CommandQueue::GetTimestampFrequency; ID3D12Fence::GetCompletedValue (d3d12.h).</summary>
    private const int EndQuerySlot = 53;
    private const int ResolveQueryDataSlot = 54;
    private const int TimestampFrequencySlot = 16;
    private const int CompletedValueSlot = 8;

    /// <summary>D3D12_QUERY_TYPE_TIMESTAMP.</summary>
    private const int TimestampQuery = 2;

    /// <summary>A block submitted this recently may still be written by its list: a new recording skips it.</summary>
    private const long BusyMilliseconds = 2000;

    private const int TakeTries = 16;
    private const int CollectMilliseconds = 50;
    private const int PassesShown = 10;
    private const double MillisecondsPerSecond = 1000.0;

    /// <summary>A pipeline name is up to two programs joined by " + "; a program's name is sgd1_hash_t32_name_hash_Wrapped.</summary>
    private const int PipelineParts = 2;
    private const int PrefixParts = 3;
    private const int SuffixParts = 2;

    private const int Graphics = 1;
    private const int Compute = 2;

    private static readonly ConcurrentDictionary<nint, ListTimes> Lists = new();
    private static readonly ConcurrentQueue<Submission> Submitted = new();
    private static readonly ConcurrentDictionary<nint, ulong> Frequencies = new();
    private static readonly Dictionary<(int Queue, string Pass), double> PassMilliseconds = [];
    private static readonly Dictionary<int, double> QueueBusy = [];
    private static readonly object Gate = new();
    private static readonly int[] Generations = new int[Blocks];
    private static readonly long[] LastUse = new long[Blocks];
    private static bool _enabled;
    private static ID3D12QueryHeap? _heap;
    private static ID3D12Resource? _readback;
    private static ulong* _values;
    private static int _next;
    private static long _untimed;
    private static long _stale;
    private static long _reportedPresents;
    private static int _collecting;

    /// <summary>The name of a desktop pipeline (<see cref="QueueProgress.PipelineName"/>); replaced in tests.</summary>
    public static Func<nint, string> Name { get; set; } = QueueProgress.PipelineName;

    /// <summary>Whether a device has the queries.</summary>
    public static bool Active => _heap is not null;

    /// <summary>Turns the timings on (or leaves them off) for the device created next.</summary>
    public static void Enable(bool enabled)
    {
        _enabled = enabled;
    }

    /// <summary>After device creation: the query heap, the readback buffer and the collecting thread, when enabled.</summary>
    public static void Attach(nint device, LoaderLog? log)
    {
        if (!_enabled || _heap is not null)
        {
            return;
        }

        Marshal.AddRef(device);
        using var desktop = new ID3D12Device(device);
        _heap = desktop.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription(QueryHeapType.Timestamp, Queries));
        _readback = desktop.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None, Vortice.Direct3D12.ResourceDescription.Buffer(Queries * sizeof(ulong)), ResourceStates.CopyDest);
        void* data;
        _readback.Map(0, null, &data).CheckError();

        _values = (ulong*)data;

        if (Interlocked.Exchange(ref _collecting, 1) == 0)
        {
            new Thread(Collect) { IsBackground = true, Name = "fivestar gpu timings" }.Start();
        }

        log?.Info($"  [gpu] timings on: a timestamp per pass in {Blocks} blocks of {QueriesPerBlock}, read {CollectMilliseconds} ms after their fence");
    }

    /// <summary>Drops the queries and every list's state (tests: another device must not record into this heap).</summary>
    public static void Detach()
    {
        lock (Gate)
        {
            _enabled = false;
            _heap?.Dispose();
            _readback?.Dispose();
            _heap = null;
            _readback = null;
            _values = null;

            Lists.Clear();
            Submitted.Clear();
            Frequencies.Clear();
            PassMilliseconds.Clear();
            QueueBusy.Clear();
        }
    }

    /// <summary>At Reset: the list's next recording takes a new block at its first work.</summary>
    [HotPath]
    public static void Reset(nint list)
    {
        if (_heap is not null && Lists.TryGetValue(list, out var times))
        {
            times.Start();
        }
    }

    /// <summary>OMSetRenderTargets: the next work begins a pass.</summary>
    [HotPath]
    public static void Targets(nint list)
    {
        if (_heap is not null && Lists.TryGetValue(list, out var times))
        {
            times.Boundary = true;
        }
    }

    /// <summary>Before a draw, dispatch or ExecuteIndirect recorded into <paramref name="desktop"/>: a timestamp when it begins a pass.</summary>
    [HotPath]
    public static void Work(nint list, nint desktop, bool compute, nint pipeline)
    {
        if (_heap is null)
        {
            return;
        }

        var times = Times(list);
        int kind = compute ? Compute : Graphics;

        if (times.Block < 0 && !Take(times))
        {
            return;
        }

        bool begins = times.Used == 0 || ((times.Boundary || kind != times.Kind) && times.Used < QueriesPerBlock - 1);

        if (!begins)
        {
            return;
        }

        EndQuery(desktop, (times.Block * QueriesPerBlock) + times.Used);
        times.Pipelines[times.Used] = pipeline;
        times.Used++;
        times.Kind = kind;
        times.Boundary = false;
    }

    /// <summary>At Close, before the desktop Close: the end timestamp and the resolve of the list's block.</summary>
    [HotPath]
    public static void BeforeClose(nint list, nint desktop)
    {
        if (_heap is null || !Lists.TryGetValue(list, out var times) || times.Used == 0 || times.Block < 0)
        {
            return;
        }

        int first = times.Block * QueriesPerBlock;
        EndQuery(desktop, first + times.Used);
        var resolve = (delegate* unmanaged<nint, nint, int, uint, uint, nint, ulong, void>)ComObject.Slot(desktop, ResolveQueryDataSlot);
        resolve(desktop, _heap.NativePointer, TimestampQuery, (uint)first, (uint)times.Used + 1, _readback!.NativePointer, (ulong)first * sizeof(ulong));
        Freeze(times);
    }

    /// <summary>The list's timing state, made at its first work.</summary>
    private static ListTimes Times(nint list)
    {
        return Lists.GetOrAdd(list, static _ => new ListTimes());
    }

    /// <summary>What the recording's Close resolved: its block and generation, and each pass's first pipeline.</summary>
    private static void Freeze(ListTimes times)
    {
        times.Closed = new ClosedRecording(times.Block, Generations[times.Block], times.Pipelines[..times.Used]);
    }

    /// <summary>
    /// After a submission on a queue (number <paramref name="number"/>, desktop queue
    /// <paramref name="desktopQueue"/>): its lists' passes are read once <paramref name="fence"/> reaches <paramref name="value"/>.
    /// </summary>
    [HotPath]
    public static void Executed(int number, nint desktopQueue, nint fence, ulong value, ReadOnlySpan<nint> lists)
    {
        if (_heap is null)
        {
            return;
        }

        ulong frequency = Frequencies.GetOrAdd(desktopQueue, Frequency);
        long now = Environment.TickCount64;

        foreach (nint list in lists)
        {
            if (Lists.TryGetValue(list, out var times) && times.Closed is not null)
            {
                Volatile.Write(ref LastUse[times.Closed.Block], now);
                Submitted.Enqueue(new Submission(number, frequency, fence, value, times.Closed));
            }
        }
    }

    /// <summary>
    /// "gpu over N frames: queue #1 busy X ms/frame: pass A ms, ...; ..." for the passes read since the last report,
    /// per frame of <paramref name="presents"/> (the presents so far); then starts over.
    /// </summary>
    public static string Report(long presents)
    {
        long frames = Math.Max(1, presents - Interlocked.Exchange(ref _reportedPresents, presents));
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"gpu over {frames} frames");

        lock (Gate)
        {
            string separator = ": ";

            foreach (var queue in QueueBusy.Keys.Order())
            {
                var passes = PassMilliseconds.Where(entry => entry.Key.Queue == queue).OrderByDescending(entry => entry.Value).Take(PassesShown);
                string shown = string.Join(", ", passes.Select(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.Key.Pass} {entry.Value / frames:F2} ms")));
                text.Append(CultureInfo.InvariantCulture, $"{separator}queue #{queue} busy {QueueBusy[queue] / frames:F2} ms/frame ({shown})");
                separator = "; ";
            }

            PassMilliseconds.Clear();
            QueueBusy.Clear();
        }

        text.Append(CultureInfo.InvariantCulture, $"; {Interlocked.Exchange(ref _untimed, 0)} recordings without a free block, {Interlocked.Exchange(ref _stale, 0)} read after their block was reused");

        return text.ToString();
    }

    /// <summary>
    /// A pass by its first pipeline: the first program's name without the container prefix (sgd1_hash_t32_) and the
    /// hash suffix (_hash_Wrapped), e.g. "meshlet_culling~CS_BatchCullPreSkinnedMeshlets" or "PS_DeferredPedTexturedNC".
    /// </summary>
    public static string PassName(string pipeline)
    {
        string first = pipeline.Split(" + ", PipelineParts)[0];
        string[] parts = first.Split('_');
        int start = 0;
        int end = parts.Length;

        if (parts.Length > PrefixParts && parts[0].StartsWith("sgd", StringComparison.Ordinal))
        {
            start = PrefixParts;
        }

        if (end - start > SuffixParts && parts[end - 1] == "Wrapped")
        {
            end -= SuffixParts;
        }

        return string.Join('_', parts[start..end]);
    }

    /// <summary>Reads every submission whose fence passed (the collecting thread, or a test).</summary>
    public static void CollectNow()
    {
        int count = Submitted.Count;

        for (int i = 0; i < count && Submitted.TryDequeue(out var submission); i++)
        {
            ulong completed = ((delegate* unmanaged<nint, ulong>)ComObject.Slot(submission.Fence, CompletedValueSlot))(submission.Fence);

            if (completed == ulong.MaxValue)
            {
                continue;
            }

            if (completed < submission.Value)
            {
                Submitted.Enqueue(submission);

                continue;
            }

            Read(submission);
        }
    }

    private static void Collect()
    {
        while (true)
        {
            Thread.Sleep(CollectMilliseconds);
            CollectNow();
        }
    }

    /// <summary>The passes of one finished submission into the sums; none when its block went to a newer recording.</summary>
    private static void Read(Submission submission)
    {
        var recording = submission.Recording;

        if (Volatile.Read(ref Generations[recording.Block]) != recording.Generation || submission.Frequency == 0)
        {
            Interlocked.Increment(ref _stale);

            return;
        }

        if (_values == null)
        {
            return;
        }

        ulong* values = _values + (recording.Block * QueriesPerBlock);
        int passes = recording.Pipelines.Length;

        lock (Gate)
        {
            for (int i = 0; i < passes; i++)
            {
                if (values[i + 1] < values[i] || values[i] == 0)
                {
                    return;
                }
            }

            for (int i = 0; i < passes; i++)
            {
                var key = (submission.Queue, PassName(Name(recording.Pipelines[i])));
                PassMilliseconds[key] = PassMilliseconds.GetValueOrDefault(key) + Milliseconds(values[i + 1] - values[i], submission.Frequency);
            }

            QueueBusy[submission.Queue] = QueueBusy.GetValueOrDefault(submission.Queue) + Milliseconds(values[passes] - values[0], submission.Frequency);
        }
    }

    private static double Milliseconds(ulong ticks, ulong frequency)
    {
        return ticks * MillisecondsPerSecond / frequency;
    }

    /// <summary>The next block not submitted lately, for a new recording; false when none of the next few is free.</summary>
    private static bool Take(ListTimes times)
    {
        long now = Environment.TickCount64;

        for (int i = 0; i < TakeTries; i++)
        {
            int block = (int)((uint)Interlocked.Increment(ref _next) % Blocks);

            if (now - Volatile.Read(ref LastUse[block]) < BusyMilliseconds && Volatile.Read(ref LastUse[block]) != 0)
            {
                continue;
            }

            Interlocked.Increment(ref Generations[block]);
            Volatile.Write(ref LastUse[block], now);
            times.Block = block;

            return true;
        }

        Interlocked.Increment(ref _untimed);

        return false;
    }

    private static void EndQuery(nint desktop, int index)
    {
        ((delegate* unmanaged<nint, nint, int, uint, void>)ComObject.Slot(desktop, EndQuerySlot))(desktop, _heap!.NativePointer, TimestampQuery, (uint)index);
    }

    /// <summary>The queue's timestamp ticks per second; 0 when it has none (a copy queue without timestamps).</summary>
    private static ulong Frequency(nint queue)
    {
        ulong frequency = 0;
        _ = ((delegate* unmanaged<nint, ulong*, int>)ComObject.Slot(queue, TimestampFrequencySlot))(queue, &frequency);

        return frequency;
    }

    /// <summary>One list's timing state: its block, the passes it began, and what its last Close resolved.</summary>
    private sealed class ListTimes
    {
        public int Block = -1;
        public int Used;
        public int Kind;
        public bool Boundary;
        public ClosedRecording? Closed;

        public nint[] Pipelines { get; } = new nint[QueriesPerBlock];

        /// <summary>A new recording: a new block at its first work.</summary>
        public void Start()
        {
            Block = -1;
            Used = 0;
            Kind = 0;
            Boundary = false;
            Closed = null;
        }
    }

    /// <summary>A closed recording's block (and its generation then) and the first pipeline of each pass.</summary>
    private sealed record ClosedRecording(int Block, int Generation, nint[] Pipelines);

    private readonly record struct Submission(int Queue, ulong Frequency, nint Fence, ulong Value, ClosedRecording Recording);
}
