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
/// in one command list between render-target changes, a switch between draws and dispatches, or a change of compute
/// pipeline (each compute program is its own pass), or a split at a memory wait (the queue's wait between the list's segments is a
/// pass of its own, "(memory wait)"), named by its first
/// pipeline (<see cref="PassName"/>). Each pass begins with a timestamp query and the list's Close ends the last; Close
/// resolves the list's block of <see cref="QueriesPerBlock"/> queries into a readback buffer. Each submission queues
/// the lists' passes with the queue fence value they end at, and a thread reads them once the fence passed (no wait
/// on the GPU): durations are summed per queue and pass, and <see cref="Report"/> prints them per frame with the
/// sample line, the 10 largest per queue and the queue's busy time. One query pair per pass, not per draw. Each
/// recording's span is also placed on the CPU clock (the queue's clock calibration), so the report adds how long any
/// queue had work in the time all queues' work spanned, and how long none had: what the per-queue sums cannot tell
/// when queues overlap or wait for each other (w95a: queue #1 busy 247-562 ms of 700-1600 ms frames).
/// </summary>
internal static unsafe class GpuTimings
{
    /// <summary>Query blocks in the ring, one per recording; at about 300 recordings a frame and 1-2 frames a second the ring turns every 4-7 s.</summary>
    private const int Blocks = 2048;

    /// <summary>
    /// Queries per recording: up to 255 passes and the end; later boundaries extend the last pass. An indirect execution
    /// with its argument clamp takes two passes, so a list of a hundred of them fits.
    /// </summary>
    private const int QueriesPerBlock = 256;

    private const int Queries = Blocks * QueriesPerBlock;

    /// <summary>ID3D12GraphicsCommandList::EndQuery and ::ResolveQueryData; ID3D12CommandQueue::GetTimestampFrequency; ID3D12Fence::GetCompletedValue (d3d12.h).</summary>
    private const int EndQuerySlot = 53;
    private const int ResolveQueryDataSlot = 54;
    private const int TimestampFrequencySlot = 16;
    private const int ClockCalibrationSlot = 17;
    private const int CompletedValueSlot = 8;

    /// <summary>A queue's clock calibration is taken again after this long, so the GPU clock's drift stays small.</summary>
    private const long CalibrationMilliseconds = 1000;

    /// <summary>D3D12_QUERY_TYPE_TIMESTAMP.</summary>
    private const int TimestampQuery = 2;

    /// <summary>A block submitted this recently may still be written by its list: a new recording skips it.</summary>
    private const long BusyMilliseconds = 2000;

    private const int TakeTries = 16;
    private const int CollectMilliseconds = 50;
    private const int PassesShown = 10;

    /// <summary>The name suffix of a recording's last pass when later passes had no query left and were added to it.</summary>
    private const string RestSuffix = " +rest";
    private const double MillisecondsPerSecond = 1000.0;

    /// <summary>A pipeline name is up to two programs joined by " + "; a program's name is sgd1_hash_t32_name_hash_Wrapped.</summary>
    private const int PipelineParts = 2;
    private const int PrefixParts = 3;
    private const int SuffixParts = 2;

    private const int Graphics = 1;
    private const int Compute = 2;
    private const int Waiting = 3;

    /// <summary>The pass name of the time between a list's segments (<see cref="Split"/>).</summary>
    private const string MemoryWaitPass = "(memory wait)";

    /// <summary>The pipeline slot of a pass that begins where a list splits at a memory wait.</summary>
    private static readonly nint MemoryWait = -1;

    /// <summary>The pass name of our argument clamp before an indirect execution (<see cref="IndirectClamp"/>).</summary>
    private const string IndirectClampPass = "loader:indirect-clamp";

    /// <summary>The pipeline slot of <see cref="IndirectClampPass"/>: pass it to <see cref="Work"/> before the clamp's dispatch.</summary>
    public static readonly nint IndirectClampPipeline = -2;

    private static readonly ConcurrentDictionary<nint, ListTimes> Lists = new();
    private static readonly ConcurrentQueue<Submission> Submitted = new();
    private static readonly ConcurrentDictionary<nint, QueueClock> Clocks = new();
    private static readonly List<(long Start, long End)> Spans = [];
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
    private static long _overflowed;
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
        WorkShapes.Attach(desktop);

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
            Clocks.Clear();
            PassMilliseconds.Clear();
            QueueBusy.Clear();
            Spans.Clear();
            WorkShapes.Detach();
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

        bool switches = times.Used == 0 || Switches(times, kind, pipeline);

        if (!switches)
        {
            return;
        }

        // Past the block's last pass, later work extends it: its name says so (Overflow).
        if (times.Used == QueriesPerBlock - 1)
        {
            times.Overflow = true;

            return;
        }

        EndQuery(desktop, (times.Block * QueriesPerBlock) + times.Used);
        times.Pipelines[times.Used] = pipeline;
        times.Used++;
        times.Kind = kind;
        times.Boundary = false;
    }

    /// <summary>
    /// <c>ListSplits.Split</c>, before the segment <paramref name="desktop"/> of <paramref name="list"/> closes at a
    /// memory wait: the current pass ends there and a "(memory wait)" pass begins, which the next work ends. The queue
    /// waits between the segments, so that time is the wait's (and the barriers around it), not the last timed pass's:
    /// with the scattering passes skipped, a screen-probe pass took a wait's 250 ms or more this way (w81, w84). Nothing
    /// when no pass began yet in this recording.
    /// </summary>
    [HotPath]
    public static void Split(nint list, nint desktop)
    {
        if (_heap is null || !Lists.TryGetValue(list, out var times) || times.Block < 0 || times.Used == 0)
        {
            return;
        }

        if (times.Used == QueriesPerBlock - 1)
        {
            times.Overflow = true;

            return;
        }

        EndQuery(desktop, (times.Block * QueriesPerBlock) + times.Used);
        times.Pipelines[times.Used] = MemoryWait;
        times.Used++;
        times.Kind = Waiting;
        times.Boundary = true;
    }

    /// <summary>
    /// Whether work of <paramref name="kind"/> with <paramref name="pipeline"/> ends the list's current pass: new targets,
    /// a draw/dispatch switch, or another compute pipeline (a run of dispatches would otherwise all count under its first
    /// program, as a cheap clear ahead of a frame's compute work did).
    /// </summary>
    private static bool Switches(ListTimes times, int kind, nint pipeline)
    {
        return times.Boundary || kind != times.Kind || (kind == Compute && pipeline != times.Pipelines[times.Used - 1]);
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
        times.Closed = new ClosedRecording(times.Block, Generations[times.Block], times.Pipelines[..times.Used], times.Overflow);
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

        var clock = Clock(desktopQueue);
        long now = Environment.TickCount64;

        foreach (nint list in lists)
        {
            if (Lists.TryGetValue(list, out var times) && times.Closed is not null)
            {
                Volatile.Write(ref LastUse[times.Closed.Block], now);
                Submitted.Enqueue(new Submission(number, clock, fence, value, times.Closed));
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
        var shownPasses = new HashSet<string>(StringComparer.Ordinal);

        lock (Gate)
        {
            string separator = ": ";

            foreach (var queue in QueueBusy.Keys.Order())
            {
                var passes = PassMilliseconds.Where(entry => entry.Key.Queue == queue).OrderByDescending(entry => entry.Value).Take(PassesShown).ToList();
                shownPasses.UnionWith(passes.Select(entry => entry.Key.Pass));
                string shown = string.Join(", ", passes.Select(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.Key.Pass} {entry.Value / frames:F2} ms")));
                text.Append(CultureInfo.InvariantCulture, $"{separator}queue #{queue} busy {QueueBusy[queue] / frames:F2} ms/frame ({shown})");
                separator = "; ";
            }

            var (busy, spanned) = Union(Spans);
            text.Append(CultureInfo.InvariantCulture, $"; all queues: some queue busy {CpuMilliseconds(busy) / frames:F2} of the {CpuMilliseconds(spanned) / frames:F2} ms/frame their work spanned, none {CpuMilliseconds(spanned - busy) / frames:F2}");

            PassMilliseconds.Clear();
            QueueBusy.Clear();
            Spans.Clear();
        }

        text.Append(WorkShapes.Report(frames, shownPasses, Named));
        text.Append(CultureInfo.InvariantCulture, $"; {Interlocked.Exchange(ref _untimed, 0)} recordings without a free block, {Interlocked.Exchange(ref _stale, 0)} read after their block was reused, {Interlocked.Exchange(ref _overflowed, 0)} with more than {QueriesPerBlock - 1} passes (the last named \"{RestSuffix.Trim()}\" holds the rest)");

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

        if (Volatile.Read(ref Generations[recording.Block]) != recording.Generation || submission.Clock.Frequency == 0)
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

            if (recording.Overflow)
            {
                Interlocked.Increment(ref _overflowed);
            }

            for (int i = 0; i < passes; i++)
            {
                string pass = Named(recording.Pipelines[i]);

                if (recording.Overflow && i == passes - 1)
                {
                    pass += RestSuffix;
                }

                var key = (submission.Queue, pass);
                PassMilliseconds[key] = PassMilliseconds.GetValueOrDefault(key) + Milliseconds(values[i + 1] - values[i], submission.Clock.Frequency);
            }

            QueueBusy[submission.Queue] = QueueBusy.GetValueOrDefault(submission.Queue) + Milliseconds(values[passes] - values[0], submission.Clock.Frequency);
            Spans.Add((submission.Clock.ToCpu(values[0]), submission.Clock.ToCpu(values[passes])));
        }
    }

    /// <summary>A pass's name: the memory wait's and the argument clamp's own, else by its first pipeline.</summary>
    private static string Named(nint pipeline)
    {
        if (pipeline == MemoryWait)
        {
            return MemoryWaitPass;
        }

        if (pipeline == IndirectClampPipeline)
        {
            return IndirectClampPass;
        }

        return PassName(Name(pipeline));
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

    /// <summary>
    /// The queue's timestamp frequency and a clock calibration (a GPU timestamp and the CPU's QueryPerformanceCounter at
    /// the same moment), taken again once older than <see cref="CalibrationMilliseconds"/>; frequency 0 when the queue
    /// has no timestamps (a copy queue without them).
    /// </summary>
    private static QueueClock Clock(nint queue)
    {
        long now = Environment.TickCount64;

        if (Clocks.TryGetValue(queue, out var clock) && now - clock.Taken < CalibrationMilliseconds)
        {
            return clock;
        }

        ulong frequency = 0;
        ulong gpu = 0;
        ulong cpu = 0;
        _ = ((delegate* unmanaged<nint, ulong*, int>)ComObject.Slot(queue, TimestampFrequencySlot))(queue, &frequency);
        _ = ((delegate* unmanaged<nint, ulong*, ulong*, int>)ComObject.Slot(queue, ClockCalibrationSlot))(queue, &gpu, &cpu);

        clock = new QueueClock(frequency, gpu, (long)cpu, now);
        Clocks[queue] = clock;

        return clock;
    }

    /// <summary>The time some span covers, and the time from the first start to the last end, in CPU ticks.</summary>
    internal static (long Busy, long Spanned) Union(List<(long Start, long End)> spans)
    {
        if (spans.Count == 0)
        {
            return (0, 0);
        }

        spans.Sort();
        long busy = 0;
        long first = spans[0].Start;
        var (start, end) = spans[0];

        foreach (var span in spans)
        {
            if (span.Start > end)
            {
                busy += end - start;
                (start, end) = span;
            }
            else if (span.End > end)
            {
                end = span.End;
            }
        }

        busy += end - start;

        return (busy, end - first);
    }

    private static double CpuMilliseconds(long ticks)
    {
        return ticks * MillisecondsPerSecond / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>One list's timing state: its block, the passes it began, and what its last Close resolved.</summary>
    private sealed class ListTimes
    {
        public int Block = -1;
        public int Used;
        public int Kind;
        public bool Boundary;
        public bool Overflow;
        public ClosedRecording? Closed;

        public nint[] Pipelines { get; } = new nint[QueriesPerBlock];

        /// <summary>A new recording: a new block at its first work.</summary>
        public void Start()
        {
            Block = -1;
            Used = 0;
            Kind = 0;
            Boundary = false;
            Overflow = false;
            Closed = null;
        }
    }

    /// <summary>A closed recording's block (and its generation then), the first pipeline of each pass, and whether its last pass holds the work past the block's passes.</summary>
    private sealed record ClosedRecording(int Block, int Generation, nint[] Pipelines, bool Overflow);

    private readonly record struct Submission(int Queue, QueueClock Clock, nint Fence, ulong Value, ClosedRecording Recording);

    /// <summary>A queue's timestamp frequency, a GPU timestamp and the CPU's QueryPerformanceCounter then, and when it was taken.</summary>
    private readonly record struct QueueClock(ulong Frequency, ulong Gpu, long Cpu, long Taken)
    {
        /// <summary>A GPU timestamp of this queue on the CPU's clock.</summary>
        public long ToCpu(ulong timestamp)
        {
            double seconds = ((long)timestamp - (long)Gpu) / (double)Frequency;

            return Cpu + (long)(seconds * System.Diagnostics.Stopwatch.Frequency);
        }
    }
}
