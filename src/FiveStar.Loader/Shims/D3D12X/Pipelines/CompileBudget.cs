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
/// to take (their estimates less the process's private growth since compiles began). Compiles of less than
/// <see cref="TinyShaderBytes"/> of DXIL are counted but never wait, so a small compute pipeline made on the game's
/// thread is not held up; while an urgent compile waits (<see cref="Urgent"/>), no other starts. Two ceilings bound
/// the loader itself (<see cref="Configure"/>): at most <see cref="MaxCompiles"/> larger compiles at once, and none
/// starts while the private bytes of the process (less its write-combined memory) and the compile host, the running
/// ones' remaining need and its own
/// estimate would pass <see cref="CeilingBytes"/>; one always may when none runs, so builds go on. Program work waits
/// while the compile host is large (<see cref="RoomForPrograms"/>), in a boot and in the warm-up alike. A cold boot to 450 s reached 40 GB
/// private, 8 GB of it in the arenas of the 7 compiles that started at the story load.
/// </summary>
internal static unsafe class CompileBudget
{
    /// <summary>DXIL below which a compile is not held back.</summary>
    public const long TinyShaderBytes = 256L << 10;

    /// <summary>The default of <see cref="MaxCompiles"/> (<c>--max-driver-compiles</c>).</summary>
    public const int DefaultMaxCompiles = 4;

    /// <summary>The default of <see cref="CeilingBytes"/> (<c>--compile-ceiling</c>, in GB).</summary>
    public const long DefaultCeilingBytes = 28L << 30;

    /// <summary>Driver memory expected per byte of DXIL: 3 MB of mesh-shader DXIL passed 8 GB.</summary>
    private const long BytesPerShaderByte = 4096;

    private const long MinimumEstimate = 2L << 30;
    private const long MaximumEstimate = 32L << 30;
    private const int PauseMilliseconds = 500;
    private const double BytesPerGigabyte = 1L << 30;
    private const double BytesPerMegabyte = 1L << 20;

    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);
    private static readonly ConcurrentDictionary<BuildMeasure, byte> Measures = new();
    private static readonly Lazy<Timer> Sampler = new(() => new Timer(_ => Sample(), null, SampleInterval, SampleInterval));

    private static readonly object Gate = new();
    private static int _running;
    private static int _runningLarge;
    private static long _expected;
    private static long _baseline;
    private static int _waiting;
    private static int _urgentWaiting;

    [ThreadStatic]
    private static bool _urgentThread;

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
        var measure = new BuildMeasure(shaderBytes, PrivateBytes(), Running, System.Diagnostics.Stopwatch.GetTimestamp());
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

        long grown = Math.Max(measure.Peak, PrivateBytes()) - measure.StartBytes;
        double seconds = System.Diagnostics.Stopwatch.GetElapsedTime(measure.Started).TotalSeconds;
        ShimContext.Runtime.Log.Detail($"  [budget] {what}: {measure.ShaderBytes / BytesPerMegabyte:F2} MB of DXIL, estimate {Estimate(measure.ShaderBytes) / BytesPerGigabyte:F1} GB, private peak +{grown / BytesPerGigabyte:F2} GB over {seconds:F0} s, {measure.Running} compiles running at its start");
    }

    private static void Sample()
    {
        if (Measures.IsEmpty)
        {
            return;
        }

        long bytes = PrivateBytes();

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
        long estimate = Estimate(shaderBytes);
        bool urgent = _urgentThread;
        bool counted = false;
        using var wait = BlockingWaits.Enter("driver compile memory budget");

        while (!TryEnter(estimate, shaderBytes < TinyShaderBytes, urgent))
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
            _expected -= Estimate(shaderBytes);

            if (shaderBytes >= TinyShaderBytes)
            {
                _runningLarge--;
            }
        }
    }

    /// <summary>Whether memory covers the reserves, <paramref name="estimate"/> and the running compiles' remaining need.</summary>
    public static bool Room(long estimate, ulong availablePhysical, ulong commitHeadroom, long remaining)
    {
        ulong need = (ulong)(estimate + remaining);

        return availablePhysical >= WarmMemoryGuard.PhysicalReserveBytes + need && commitHeadroom >= WarmMemoryGuard.ReserveBytes + need;
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

        return runningLarge < maxCompiles && privateBytes + remaining + estimate <= ceilingBytes;
    }

    private static bool TryEnter(long estimate, bool tiny, bool urgent)
    {
        lock (Gate)
        {
            if (!tiny && !urgent && Volatile.Read(ref _urgentWaiting) > 0)
            {
                return false;
            }

            long privateBytes = PrivateBytes();

            if (_running == 0)
            {
                _baseline = privateBytes;
            }

            long remaining = Math.Max(0, _expected - Math.Max(0, privateBytes - _baseline));

            if (!tiny && !Room(estimate, WarmMemoryGuard.AvailablePhysical(), WarmMemoryGuard.CommitAvailable(), remaining))
            {
                return false;
            }

            if (!tiny && !UnderCeiling(estimate, _runningLarge, CountedBytes(privateBytes), remaining, MaxCompiles, CeilingBytes))
            {
                return false;
            }

            _running++;
            _expected += estimate;

            if (!tiny)
            {
                _runningLarge++;
            }

            return true;
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
