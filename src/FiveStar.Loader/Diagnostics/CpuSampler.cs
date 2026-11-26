// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using FiveStar.Loader.Loading;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// <c>--cpu-sample &lt;ms&gt;[:&lt;s&gt;]</c>: where the game's threads spend a frame. Every ms milliseconds a
/// <see cref="ThreadSnapshot"/> of up to <see cref="ThreadsPerTick"/> game threads in turn (threads that started in a
/// mapped image, and the mapped main thread), unwound through .pdata; each sample is placed by
/// <see cref="CpuAddressMap"/> (game, ours, system, blocked) and every s seconds a ranked table is logged, over all game
/// threads and per thread, named by <see cref="CpuSampleNames"/>. Samples are kept as addresses, so the sampler
/// allocates almost nothing between reports; its own time is reported with them.
/// </summary>
internal sealed unsafe class CpuSampler
{
    private const int ThreadsPerTick = 8;
    private const int MaxFrames = 32;
    private const long CopyBytes = 256 * 1024;
    private const int TopAll = 12;
    private const int TopThreads = 12;
    private const int TopPerThread = 4;
    private const double DefaultReportSeconds = 10;
    private const int IntervalAndReport = 2;
    private const double Percent = 100.0;
    private const string MainThreadName = "mapped main";
    private const string Prefix = "  [cpu] ";

    private static readonly CpuBucket[] Buckets = [CpuBucket.Game, CpuBucket.Ours, CpuBucket.System, CpuBucket.Blocked, CpuBucket.Unread];

    private readonly ImageTable _images;
    private readonly LoaderLog _log;
    private readonly CpuSampleNames _names;
    private readonly (CodeRange Range, string Name)[] _stubs = WaitStubs.Find();
    private readonly Func<ulong, bool> _isCaller;

    public CpuSampler(ImageTable images, CallTrace trace, LoaderLog log)
    {
        _images = images;
        _log = log;
        _names = new CpuSampleNames(images, trace, _stubs);
        _isCaller = IsGameCaller;
    }

    /// <summary>Parses <c>&lt;ms&gt;[:&lt;s&gt;]</c> (the report period defaults to 10 s); false unless both are positive.</summary>
    public static bool TryParse(string text, out TimeSpan interval, out TimeSpan report)
    {
        var parts = text.Split(':');
        double seconds = DefaultReportSeconds;
        interval = report = TimeSpan.Zero;

        if (parts.Length > IntervalAndReport || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double milliseconds) || milliseconds <= 0)
        {
            return false;
        }

        if (parts.Length == IntervalAndReport && (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) || seconds <= 0))
        {
            return false;
        }

        (interval, report) = (TimeSpan.FromMilliseconds(milliseconds), TimeSpan.FromSeconds(seconds));

        return true;
    }

    public void Start(TimeSpan interval, TimeSpan report)
    {
        var thread = new Thread(() => Run(interval, report)) { IsBackground = true, Name = "fivestar cpu sampler", Priority = ThreadPriority.AboveNormal };
        thread.Start();
        _log.Info($"{Prefix}every {interval.TotalMilliseconds} ms up to {ThreadsPerTick} game threads, a report every {report.TotalSeconds} s");
    }

    private void Run(TimeSpan interval, TimeSpan report)
    {
        for (int window = 1; ; window++)
        {
            Window(window, interval, report);
        }
    }

    /// <summary>One report period: the game threads now, sampled until it ends, then the report.</summary>
    internal void Window(int window, TimeSpan interval, TimeSpan report)
    {
        var map = Map();
        var threads = GameThreads();
        var tallies = threads.ToDictionary(id => id, _ => new Dictionary<CpuSample, int>());
        var clock = Stopwatch.StartNew();
        long busy = 0;
        int next = 0;

        while (clock.Elapsed < report)
        {
            long start = Stopwatch.GetTimestamp();
            next = Tick(map, threads, tallies, next);
            busy += Stopwatch.GetTimestamp() - start;
            Thread.Sleep(interval);
        }

        Report(window, clock.Elapsed, Stopwatch.GetElapsedTime(0, busy), interval, tallies);
    }

    /// <summary>Samples the next <see cref="ThreadsPerTick"/> threads from <paramref name="next"/>; returns where the next tick starts.</summary>
    private int Tick(CpuAddressMap map, uint[] threads, Dictionary<uint, Dictionary<CpuSample, int>> tallies, int next)
    {
        Span<ulong> pcs = stackalloc ulong[MaxFrames];
        int count = Math.Min(ThreadsPerTick, threads.Length);

        for (int i = 0; i < count; i++)
        {
            uint id = threads[(next + i) % threads.Length];
            int frames = ThreadStackCapture.Capture(id, map.Modules, pcs, CopyBytes, map.Managed, _isCaller, out ulong site);
            var sample = map.Classify(pcs[..frames], site, BlockingWaits.Current(id));
            var tally = tallies[id];
            tally[sample] = tally.GetValueOrDefault(sample) + 1;
        }

        return (next + count) % Math.Max(threads.Length, 1);
    }

    /// <summary>A return address into a mapped image's code with a call before it: the game's call into our code.</summary>
    private bool IsGameCaller(ulong value)
    {
        var image = _images.FindByAddress((nint)value);

        return image is not null
            && ReturnAddresses.InCode(image, (nint)value)
            && ReturnAddresses.CallBytesReadable((nint)value)
            && ReturnAddresses.AfterCall((byte*)value);
    }

    /// <summary>The address map now: the mapped images, the OS modules (managed assemblies apart) and the wait stubs.</summary>
    private CpuAddressMap Map()
    {
        var mapped = _images.All.Select(i => new CodeRange((ulong)i.Base, (ulong)i.Base + i.Size)).ToArray();
        var native = new List<CodeRange>();
        var all = new List<CodeRange>(mapped);

        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
        {
            var range = new CodeRange((ulong)module.BaseAddress, (ulong)module.BaseAddress + (ulong)module.ModuleMemorySize);
            all.Add(range);

            if (!ThreadProfiler.IsAssembly(module.ModuleName))
            {
                native.Add(range);
            }
        }

        return new CpuAddressMap(mapped, [.. native], [.. _stubs.Select(stub => stub.Range)], FunctionStart) { Modules = [.. all] };
    }

    /// <summary>The threads that started in a mapped image, and the mapped main thread.</summary>
    private uint[] GameThreads()
    {
        uint self = Kernel32.GetCurrentThreadId();
        var threads = new List<uint>();

        foreach (ProcessThread thread in Process.GetCurrentProcess().Threads)
        {
            uint id = (uint)thread.Id;

            if (id == self)
            {
                continue;
            }

            if (_images.FindByAddress(ManagedProfile.StartAddress(id)) is not null || ThreadStacks.Name(id) == MainThreadName)
            {
                threads.Add(id);
            }
        }

        return [.. threads];
    }

    private static ulong FunctionStart(ulong pc)
    {
        ulong imageBase = 0;
        nint function = Ntdll.RtlLookupFunctionEntry(pc, &imageBase, 0);

        if (function == 0)
        {
            return pc;
        }

        return imageBase + *(uint*)function;
    }

    private void Report(int window, TimeSpan length, TimeSpan busy, TimeSpan interval, Dictionary<uint, Dictionary<CpuSample, int>> tallies)
    {
        var all = new Dictionary<CpuSample, int>();

        foreach (var (sample, count) in tallies.Values.SelectMany(tally => tally))
        {
            all[sample] = all.GetValueOrDefault(sample) + count;
        }

        int samples = all.Values.Sum();
        _log.Info($"{Prefix}#{window} over {length.TotalSeconds:F1} s: {tallies.Count} game threads, {samples} samples (each thread every {EachThread(interval, tallies.Count):F0} ms); the sampler took {Percent * busy / length:F1} % of one core");

        if (samples == 0)
        {
            return;
        }

        var named = ByLabel(all);
        _log.Info($"{Prefix}all game threads: {Totals(named, samples)}");

        foreach (var bucket in Buckets)
        {
            if (named.TryGetValue(bucket, out var labels))
            {
                _log.Info($"{Prefix}  {Name(bucket)}: {Top(labels, samples, TopAll)}");
            }
        }

        LogThreads(tallies);
    }

    /// <summary>The busiest threads (most samples not blocked), each with its buckets and their top names.</summary>
    private void LogThreads(Dictionary<uint, Dictionary<CpuSample, int>> tallies)
    {
        var busiest = tallies
            .Select(pair => (Id: pair.Key, Tally: pair.Value, Running: pair.Value.Where(s => s.Key.Bucket is not (CpuBucket.Blocked or CpuBucket.Unread)).Sum(s => s.Value)))
            .Where(thread => thread.Running > 0)
            .OrderByDescending(thread => thread.Running)
            .Take(TopThreads);

        foreach (var (id, tally, running) in busiest)
        {
            int samples = tally.Values.Sum();
            var named = ByLabel(tally);
            var parts = Buckets
                .Where(named.ContainsKey)
                .Select(bucket => $"{Name(bucket)} {Percent * named[bucket].Values.Sum() / samples:F0} % ({Top(named[bucket], samples, TopPerThread)})");
            string name = ThreadStacks.Name(id);

            if (name.Length != 0)
            {
                name = $" \"{name}\"";
            }

            _log.Info($"{Prefix}thread {id}{name}: {samples} samples, running {Percent * running / samples:F0} %: {string.Join("; ", parts)}");
        }
    }

    private Dictionary<CpuBucket, Dictionary<string, int>> ByLabel(Dictionary<CpuSample, int> tally)
    {
        var named = new Dictionary<CpuBucket, Dictionary<string, int>>();

        foreach (var (sample, count) in tally)
        {
            if (!named.TryGetValue(sample.Bucket, out var labels))
            {
                labels = new Dictionary<string, int>(StringComparer.Ordinal);
                named[sample.Bucket] = labels;
            }

            string label = _names.Label(sample);
            labels[label] = labels.GetValueOrDefault(label) + count;
        }

        return named;
    }

    private static string Totals(Dictionary<CpuBucket, Dictionary<string, int>> named, int samples)
    {
        return string.Join(", ", Buckets.Select(bucket => $"{Name(bucket)} {Percent * Sum(named, bucket) / samples:F1} %"));
    }

    private static int Sum(Dictionary<CpuBucket, Dictionary<string, int>> named, CpuBucket bucket)
    {
        if (!named.TryGetValue(bucket, out var labels))
        {
            return 0;
        }

        return labels.Values.Sum();
    }

    private static string Top(Dictionary<string, int> labels, int samples, int top)
    {
        return string.Join(", ", labels.OrderByDescending(pair => pair.Value).Take(top).Select(pair => $"{pair.Key} {Percent * pair.Value / samples:F1} %"));
    }

    /// <summary>How often one thread is sampled: the ticks it takes to go round the threads.</summary>
    private static double EachThread(TimeSpan interval, int threads)
    {
        return interval.TotalMilliseconds * Math.Max(1, Math.Ceiling((double)threads / ThreadsPerTick));
    }

    private static string Name(CpuBucket bucket)
    {
        return bucket.ToString().ToLowerInvariant();
    }
}
