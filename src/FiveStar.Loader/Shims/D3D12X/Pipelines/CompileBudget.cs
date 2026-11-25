// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// Admits driver compiles by what they will take (docs/d3d12x-pipelines.md#stand-ins): the NVIDIA compiler grows a
/// doubling arena per pipeline (1, 2, 4, 8 GB) for minutes, so six mesh-shader pipelines of 1 to 3 MB of DXIL that
/// started together took 50 GB. Each compile is expected to take <see cref="Estimate"/>; a new one starts only while
/// free physical memory and commit cover the reserves, the new estimate and what the running ones are still expected
/// to take (their whole estimates). Compiles of less than
/// <see cref="TinyShaderBytes"/> of DXIL are counted but never wait, so a small compute pipeline made on the game's
/// thread is not held up; while an urgent compile waits (<see cref="Urgent"/>), no other starts. Two ceilings bound
/// the loader itself (<see cref="Configure"/>): at most <see cref="MaxCompiles"/> larger compiles at once, and none
/// starts while the private bytes of the process (less its write-combined memory) and the compile host, the running
/// ones' remaining need and its own
/// estimate would pass <see cref="CeilingBytes"/> (off by default); one always may when none runs, so builds go on.
/// The physical reserve is 4 GB plus half the game's RAM-backed growth over the last minute, at most 4 GB more
/// (<see cref="GrowthAllowance"/>), so compiles leave the game part of what it is about to take. An urgent compile (a
/// command list or a game thread waits: a stand-in the game shows) needs only the 4 GB floor and its own estimate,
/// not the reserve for growth nor the running compiles' estimates; boot w21 held build threads 236 s in all by 175 s
/// with 11 to 15 GB free, behind a 7 GB estimate of one running compile and a reserve of up to 10 GB. Compiles of more than
/// <see cref="HeavyShaderBytes"/> run one at a time, and wait while the game loads (<see cref="LoadWindow"/>) unless a
/// game thread or a command list waits for them. Program work waits
/// while the compile host is large (<see cref="RoomForPrograms"/>), in a boot and in the warm-up alike. A cold boot to 450 s reached 40 GB
/// private, 8 GB of it in the arenas of the 7 compiles that started at the story load.
/// </summary>
internal static unsafe class CompileBudget
{
    /// <summary>DXIL below which a compile is not held back.</summary>
    public const long TinyShaderBytes = 256L << 10;

    /// <summary>The default of <see cref="MaxCompiles"/> (<c>--max-driver-compiles</c>).</summary>
    public const int DefaultMaxCompiles = 4;

    /// <summary>The default of <see cref="CeilingBytes"/> (<c>--compile-ceiling</c>, in GB): none, free RAM decides.</summary>
    public const long DefaultCeilingBytes = 0;

    /// <summary>
    /// DXIL above which a compile waits during the load window, unless it is urgent, and runs only while no other such
    /// compile does: in boot w19 two carry-form foliage pipelines of 2.8 MB ran side by side from 47 s and neither had
    /// finished at 245 s, against 93 s for one alone.
    /// </summary>
    public const long HeavyShaderBytes = 2L << 20;

    /// <summary>The least physical memory compiles leave free: the boot guard kills below 4 GB.</summary>
    public const long MinimumPhysicalReserve = 4L << 30;

    /// <summary>The most the game's expected growth adds to the reserve (<see cref="GrowthAllowance"/>).</summary>
    public const long MaximumGrowth = 4L << 30;

    /// <summary>
    /// Driver memory expected per byte of DXIL. Boot v3 measured 101 compiles of 256 KB to 1.94 MB (the private peak
    /// over each one's start, sampled every 250 ms): alone, at most 0.40 GB; with others running, at most 4.70 GB for
    /// 1.75 MB, 2.69 GB per MB at the most and 0.79 at the 90th percentile. The most per MB, with concurrent growth
    /// counted, keeps a margin, and puts 3 MB at the 8 GB an earlier mesh-shader compile passed.
    /// </summary>
    private const long BytesPerShaderByte = 2816;

    /// <summary>The least a compile is expected to take: alone, none measured passed 0.40 GB.</summary>
    private const long MinimumEstimate = 512L << 20;

    /// <summary>
    /// The most one compile is expected to take. The plain superstep form of the foliage mesh pipeline (13 MB of DXIL)
    /// took about 20 GB of nvgpucomp64 arenas by itself; with the superstep carry (on by default) it is 2.75 MB and took
    /// 6.4 GB, and the largest forms sampled are under 3 MB.
    /// </summary>
    private const long MaximumEstimate = 16L << 30;
    private const int PauseMilliseconds = 500;
    private const double BytesPerGigabyte = 1L << 30;
    private const double BytesPerMegabyte = 1L << 20;

    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan GrowthInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GrowthSpan = TimeSpan.FromSeconds(60);
    private static readonly Queue<long> GrowthSamples = new();
    private static readonly Lazy<Timer> GrowthSampler = new(() => new Timer(_ => SampleGrowth(), null, TimeSpan.Zero, GrowthInterval));
    private static long _growthAllowance;
    private static readonly ConcurrentDictionary<BuildMeasure, byte> Measures = new();
    private static readonly Lazy<Timer> Sampler = new(() => new Timer(_ => Sample(), null, SampleInterval, SampleInterval));

    private static readonly object Gate = new();
    private static int _running;
    private static int _runningLarge;
    private static int _runningHeavy;
    private static long _expected;
    private static int _waiting;
    private static int _urgentWaiting;

    [ThreadStatic]
    private static bool _urgentThread;

    [ThreadStatic]
    private static bool _noWaitThread;

    /// <summary>
    /// Whether this thread must not wait for room (a game thread building at first use): <see cref="TryEnterNow"/>
    /// then decides at once (<see cref="PipelineBuildQueue.RunNow"/> sets it).
    /// </summary>
    public static bool NoWait
    {
        get => _noWaitThread;
        set => _noWaitThread = value;
    }

    /// <summary>Counts the compile when it may start now; false, without waiting, when it may not. Pair a true with <see cref="Exit"/>.</summary>
    public static bool TryEnterNow(long shaderBytes)
    {
        return TryEnter(shaderBytes, _urgentThread || _noWaitThread);
    }

    /// <summary>
    /// Whether this thread's compiles are urgent (a command list or a game thread waits for them): while one waits, no
    /// other compile is admitted (<see cref="PipelineBuildQueue"/> sets it around each creation).
    /// </summary>
    public static bool Urgent
    {
        get => _urgentThread;
        set => _urgentThread = value;
    }

    /// <summary>The most compiles of <see cref="TinyShaderBytes"/> or more running at once.</summary>
    public static int MaxCompiles { get; private set; } = DefaultMaxCompiles;

    /// <summary>The process private bytes compiles are kept under.</summary>
    public static long CeilingBytes { get; private set; } = DefaultCeilingBytes;

    /// <summary>Sets the ceilings (<c>--max-driver-compiles</c>, <c>--compile-ceiling</c>); call before the first compile.</summary>
    public static void Configure(int maxCompiles, long ceilingBytes)
    {
        MaxCompiles = Math.Max(1, maxCompiles);
        CeilingBytes = ceilingBytes;
    }

    /// <summary>Whether the game is in its load window (the loading screen is up, or the boot is young); none by default.</summary>
    public static Func<bool> LoadWindow { get; set; } = static () => false;

    /// <summary>
    /// What the game's growth adds to the reserve: half its RAM-backed growth over the last minute (the load grows
    /// fastest, and compiles running count in it too), at most <see cref="MaximumGrowth"/>.
    /// </summary>
    public static long GrowthAllowance(long grownOverMinute)
    {
        return Math.Clamp(grownOverMinute >> 1, 0, MaximumGrowth);
    }

    /// <summary>The physical memory compiles leave free: 4 GB and the game's expected growth.</summary>
    public static long PhysicalReserve(long growthAllowance)
    {
        return MinimumPhysicalReserve + growthAllowance;
    }

    /// <summary>Whether a compile of <paramref name="shaderBytes"/> waits for the load window to end.</summary>
    public static bool HeldForLoad(long shaderBytes, bool urgent, bool loadWindow)
    {
        return loadWindow && !urgent && IsHeavy(shaderBytes);
    }

    /// <summary>Whether a compile of <paramref name="shaderBytes"/> is heavy (<see cref="HeavyShaderBytes"/>).</summary>
    public static bool IsHeavy(long shaderBytes)
    {
        return shaderBytes > HeavyShaderBytes;
    }

    /// <summary>Whether a compile may start beside <paramref name="runningHeavy"/> heavy ones: a heavy one only alone.</summary>
    public static bool HeavyRoom(long shaderBytes, int runningHeavy)
    {
        return !IsHeavy(shaderBytes) || runningHeavy == 0;
    }

    /// <summary>Private bytes of helper processes that count toward the ceiling: the compile host (<see cref="CompileHost"/>).</summary>
    public static Func<long> HelperPrivateBytes { get; set; } = static () => 0;

    /// <summary>The compile host's private bytes above which program work runs one at a time.</summary>
    public const long HostCeilingBytes = 8L << 30;

    /// <summary>
    /// Whether program work may start beside <paramref name="running"/> others (<see cref="ProgramWorkers"/>, the
    /// warm-up's programs too): the compile host, where the programs' memory goes, is under
    /// <see cref="HostCeilingBytes"/>, or none runs. The loader's own private bytes do not hold programs back: they are
    /// the driver's and the game's, and a cold boot passes the compile ceiling while stand-ins still wait for programs.
    /// </summary>
    public static bool RoomForPrograms(int running)
    {
        return ProgramRoom(running, HelperPrivateBytes(), HostCeilingBytes);
    }

    /// <summary>The decision of <see cref="RoomForPrograms"/>.</summary>
    public static bool ProgramRoom(int running, long privateBytes, long ceilingBytes)
    {
        return running == 0 || privateBytes <= ceilingBytes;
    }

    /// <summary>Compiles running now.</summary>
    public static int Running => Volatile.Read(ref _running);

    /// <summary>The compiles running and waiting, what the running ones are expected to take, and the private bytes counted.</summary>
    public static string Summary
    {
        get
        {
            long expected = Interlocked.Read(ref _expected);

            return $"driver compiles {Running} running ({expected / BytesPerGigabyte:F1} GB expected), {Waiting} waiting; private {PrivateBytes() / BytesPerGigabyte:F1} GB, compile host {HelperPrivateBytes() / BytesPerGigabyte:F1} GB";
        }
    }

    /// <summary>The process's private bytes (the loader's own, not the compile host's).</summary>
    public static long ProcessPrivateBytes => PrivateBytes();

    /// <summary>Compiles waiting for memory before they start.</summary>
    public static int Waiting => Volatile.Read(ref _waiting);

    /// <summary>
    /// Starts measuring a compile (<see cref="Measured"/> ends it): the process's private bytes now, and their peak while
    /// it runs, sampled every <see cref="SampleInterval"/>. The peak counts the other compiles running too.
    /// </summary>
    public static BuildMeasure Measure(long shaderBytes)
    {
        var measure = new BuildMeasure(shaderBytes, RamBackedBytes(), Running, System.Diagnostics.Stopwatch.GetTimestamp());
        Measures[measure] = 0;
        _ = Sampler.Value;

        return measure;
    }

    /// <summary>
    /// Ends a measure and logs a compile of <see cref="TinyShaderBytes"/> or more: its DXIL, estimate, the private
    /// peak over its start, its time and the compiles running when it started (calibration data for <see cref="Estimate"/>).
    /// </summary>
    public static void Measured(BuildMeasure measure, string what)
    {
        Measures.TryRemove(measure, out _);

        if (measure.ShaderBytes < TinyShaderBytes)
        {
            return;
        }

        long grown = Math.Max(measure.Peak, RamBackedBytes()) - measure.StartBytes;
        double seconds = System.Diagnostics.Stopwatch.GetElapsedTime(measure.Started).TotalSeconds;
        ShimContext.Runtime.Log.Detail($"  [budget] {what}: {measure.ShaderBytes / BytesPerMegabyte:F2} MB of DXIL, estimate {Estimate(measure.ShaderBytes) / BytesPerGigabyte:F1} GB, RAM-backed private peak +{grown / BytesPerGigabyte:F2} GB over {seconds:F0} s, {measure.Running} compiles running at its start");
    }

    private static void Sample()
    {
        if (Measures.IsEmpty)
        {
            return;
        }

        long bytes = RamBackedBytes();

        foreach (var measure in Measures.Keys)
        {
            measure.Observe(bytes);
        }
    }

    /// <summary>The driver memory a compile of <paramref name="shaderBytes"/> of DXIL is expected to take.</summary>
    public static long Estimate(long shaderBytes)
    {
        return Math.Clamp(shaderBytes * BytesPerShaderByte, MinimumEstimate, MaximumEstimate);
    }

    /// <summary>Waits until the compile may start, then counts it; pair with <see cref="Exit"/>.</summary>
    public static void Enter(long shaderBytes)
    {
        bool urgent = _urgentThread || _noWaitThread;
        bool counted = false;
        using var wait = BlockingWaits.Enter("driver compile memory budget");

        while (!TryEnter(shaderBytes, urgent))
        {
            if (!counted)
            {
                Count(urgent, 1);
                counted = true;
            }

            Thread.Sleep(PauseMilliseconds);
        }

        if (counted)
        {
            Count(urgent, -1);
        }
    }

    private static void Count(bool urgent, int change)
    {
        Interlocked.Add(ref _waiting, change);

        if (urgent)
        {
            Interlocked.Add(ref _urgentWaiting, change);
        }
    }

    public static void Exit(long shaderBytes)
    {
        lock (Gate)
        {
            _running--;

            if (shaderBytes >= TinyShaderBytes)
            {
                _runningLarge--;
                _expected -= Estimate(shaderBytes);
            }

            if (IsHeavy(shaderBytes))
            {
                _runningHeavy--;
            }
        }
    }

    /// <summary>
    /// Whether memory covers the reserves (<paramref name="physicalReserve"/>, <see cref="PhysicalReserve"/>),
    /// <paramref name="estimate"/> and the running compiles' remaining need.
    /// </summary>
    public static bool Room(long estimate, ulong availablePhysical, ulong commitHeadroom, long remaining, long physicalReserve)
    {
        ulong need = (ulong)(estimate + remaining);

        return availablePhysical >= (ulong)physicalReserve + need && commitHeadroom >= WarmMemoryGuard.ReserveBytes + need;
    }

    /// <summary>
    /// Whether the loader's ceilings let one more larger compile start: fewer than <paramref name="maxCompiles"/> run,
    /// and the private bytes, the running ones' remaining need and <paramref name="estimate"/> stay within
    /// <paramref name="ceilingBytes"/>; with none running, always.
    /// </summary>
    public static bool UnderCeiling(long estimate, int runningLarge, long privateBytes, long remaining, int maxCompiles, long ceilingBytes)
    {
        if (runningLarge == 0)
        {
            return true;
        }

        return runningLarge < maxCompiles && (ceilingBytes <= 0 || privateBytes + remaining + estimate <= ceilingBytes);
    }

    private static bool TryEnter(long shaderBytes, bool urgent)
    {
        long estimate = Estimate(shaderBytes);
        bool tiny = shaderBytes < TinyShaderBytes;

        if (!tiny && HeldForLoad(shaderBytes, urgent, LoadWindow()))
        {
            return false;
        }

        _ = GrowthSampler.Value;

        lock (Gate)
        {
            if ((!tiny && !urgent && Volatile.Read(ref _urgentWaiting) > 0) || !HeavyRoom(shaderBytes, _runningHeavy))
            {
                return false;
            }

            long privateBytes = PrivateBytes();

            // The running compiles' whole estimates: their growth so far is not subtracted. It was (the private growth
            // since compiles began), but that growth was mostly textures and their video-memory mapping, so the running
            // ones counted as done and seven ran at once; boot w11b's working set rose by 20 GB in 30 s.
            long remaining = _expected;

            if (urgent)
            {
                remaining = 0;
            }

            if (!tiny && !Room(estimate, WarmMemoryGuard.AvailablePhysical(), WarmMemoryGuard.CommitAvailable(), remaining, CurrentReserve(urgent)))
            {
                return false;
            }

            if (!tiny && !UnderCeiling(estimate, _runningLarge, CountedBytes(privateBytes), remaining, MaxCompiles, CeilingBytes))
            {
                return false;
            }

            _running++;

            if (!tiny)
            {
                _runningLarge++;
                _expected += estimate;
            }

            if (IsHeavy(shaderBytes))
            {
                _runningHeavy++;
            }

            return true;
        }
    }

    /// <summary>The physical reserve now: an urgent compile (a stand-in the game shows) needs only the floor.</summary>
    private static long CurrentReserve(bool urgent)
    {
        if (urgent)
        {
            return MinimumPhysicalReserve;
        }

        return PhysicalReserve(Interlocked.Read(ref _growthAllowance));
    }

    /// <summary>
    /// Samples the loader's RAM-backed private bytes (less its write-combined memory, the driver's mapping of video
    /// memory) every 5 s; the growth over the last minute sets <see cref="GrowthAllowance"/>. Compiles running grow it
    /// too, which only makes the reserve larger.
    /// </summary>
    private static void SampleGrowth()
    {
        long bytes = RamBackedBytes();

        lock (GrowthSamples)
        {
            GrowthSamples.Enqueue(bytes);

            while (GrowthSamples.Count > (GrowthSpan / GrowthInterval) + 1)
            {
                GrowthSamples.Dequeue();
            }

            Interlocked.Exchange(ref _growthAllowance, GrowthAllowance(bytes - GrowthSamples.Min()));
        }
    }

    /// <summary>
    /// The private bytes the ceiling counts: the loader's less its write-combined memory (the driver's system-memory
    /// pools, which grew by 20 GB after the story load while compiles were held to one), plus the compile host's.
    /// </summary>
    private static long CountedBytes(long privateBytes)
    {
        return Math.Max(0, privateBytes - Diagnostics.WriteCombinedMemory.CachedBytes) + HelperPrivateBytes();
    }

    /// <summary>The loader's private bytes less its write-combined memory (the driver's mapping of video memory).</summary>
    private static long RamBackedBytes()
    {
        return Math.Max(0, PrivateBytes() - Diagnostics.WriteCombinedMemory.CachedBytes);
    }

    private static long PrivateBytes()
    {
        ProcessMemoryCountersEx counters = default;
        counters.Size = (uint)sizeof(ProcessMemoryCountersEx);

        if (Kernel32.K32GetProcessMemoryInfo(Kernel32.GetCurrentProcess(), &counters, counters.Size) == 0)
        {
            return 0;
        }

        return (long)counters.PrivateUsage;
    }
}
