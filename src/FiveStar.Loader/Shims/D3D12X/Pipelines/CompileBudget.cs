// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

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
/// thread is not held up; while an urgent compile waits (<see cref="Urgent"/>), no other starts.
/// </summary>
internal static unsafe class CompileBudget
{
    /// <summary>DXIL below which a compile is not held back.</summary>
    public const long TinyShaderBytes = 256L << 10;

    /// <summary>Driver memory expected per byte of DXIL: 3 MB of mesh-shader DXIL passed 8 GB.</summary>
    private const long BytesPerShaderByte = 4096;

    private const long MinimumEstimate = 2L << 30;
    private const long MaximumEstimate = 32L << 30;
    private const int PauseMilliseconds = 500;

    private static readonly object Gate = new();
    private static int _running;
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

    /// <summary>Compiles running now.</summary>
    public static int Running => Volatile.Read(ref _running);

    /// <summary>Compiles waiting for memory before they start.</summary>
    public static int Waiting => Volatile.Read(ref _waiting);

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
        }
    }

    /// <summary>Whether memory covers the reserves, <paramref name="estimate"/> and the running compiles' remaining need.</summary>
    public static bool Room(long estimate, ulong availablePhysical, ulong commitHeadroom, long remaining)
    {
        ulong need = (ulong)(estimate + remaining);

        return availablePhysical >= WarmMemoryGuard.PhysicalReserveBytes + need && commitHeadroom >= WarmMemoryGuard.ReserveBytes + need;
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

            _running++;
            _expected += estimate;

            return true;
        }
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
