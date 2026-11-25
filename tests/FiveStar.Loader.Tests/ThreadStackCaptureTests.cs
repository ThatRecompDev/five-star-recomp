// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// Stack capture of another thread: the unwind over the stack copy finds a sleeping thread's native frames, and
/// capturing a thread that runs managed code while garbage collections run never freezes the process (the capture
/// used to hold the thread suspended across its own P/Invokes, and a collection that started meanwhile waited for it).
/// </summary>
public sealed class ThreadStackCaptureTests
{
    private const int Frames = 64;

    [Fact]
    public void A_sleeping_thread_unwinds_from_ntdll_through_kernelbase()
    {
        var modules = Modules();
        uint id = 0;
        using var started = new ManualResetEventSlim();
        var sleeper = new Thread(() =>
        {
            id = Kernel32.GetCurrentThreadId();
            started.Set();
            Thread.Sleep(Timeout.Infinite);
        }) { IsBackground = true };

        sleeper.Start();
        started.Wait();
        Thread.Sleep(100);

        var pcs = new ulong[Frames];
        int count = ThreadStackCapture.Capture(id, modules, pcs);
        var names = pcs.Take(count).Select(pc => ModuleOf(pc)).ToList();

        Assert.True(count >= 3, $"{count} frames: {string.Join(" <- ", names)}");
        Assert.Equal("ntdll.dll", names[0]);
        Assert.Contains("kernelbase.dll", names);
    }

    /// <summary>
    /// A snapshot whose suspend or context read does not come back is skipped after the time bound and reported, and
    /// the next capture runs on a fresh worker (w83's sampler and hang watch stopped for good inside one capture).
    /// </summary>
    [Fact]
    public unsafe void A_snapshot_that_does_not_come_back_is_skipped_and_the_next_capture_works()
    {
        var modules = Modules();
        uint id = 0;
        uint stuck = 0;
        using var started = new ManualResetEventSlim();
        var sleeper = new Thread(() =>
        {
            id = Kernel32.GetCurrentThreadId();
            started.Set();
            Thread.Sleep(Timeout.Infinite);
        }) { IsBackground = true };

        sleeper.Start();
        started.Wait();

        try
        {
            ThreadSnapshot.Override = (nint)(delegate* unmanaged<byte*, void>)&Hang;
            ThreadSnapshot.Stuck = thread => stuck = thread;
            var clock = Stopwatch.StartNew();

            Assert.Equal(0, ThreadStackCapture.Capture(id, modules, new ulong[Frames]));
            Assert.True(clock.ElapsedMilliseconds < ThreadSnapshot.TimeoutMilliseconds * 8, $"{clock.ElapsedMilliseconds} ms");
            Assert.Equal(id, stuck);
        }
        finally
        {
            ThreadSnapshot.Override = 0;
            ThreadSnapshot.Stuck = null;
            Hung.Set();
        }

        Assert.True(ThreadStackCapture.Capture(id, modules, new ulong[Frames]) >= 1);
    }

    private static readonly ManualResetEventSlim Hung = new();

    /// <summary>A snapshot routine that does not come back until the test ends.</summary>
    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static unsafe void Hang(byte* arguments)
    {
        Hung.Wait();
    }

    [Fact]
    public void Capturing_a_thread_in_managed_code_during_collections_never_freezes()
    {
        var modules = Modules();
        uint id = 0;
        using var started = new ManualResetEventSlim();
        using var stop = new CancellationTokenSource();
        var spinner = new Thread(() =>
        {
            id = Kernel32.GetCurrentThreadId();
            started.Set();
            var kept = new List<byte[]>();

            while (!stop.IsCancellationRequested)
            {
                kept.Add(new byte[256]);

                if (kept.Count > 4096)
                {
                    kept.Clear();
                }
            }
        }) { IsBackground = true };
        var collector = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                GC.Collect();
            }
        }) { IsBackground = true };

        spinner.Start();
        started.Wait();
        collector.Start();

        var pcs = new ulong[Frames];
        var clock = Stopwatch.StartNew();
        int captures = 0;

        while (clock.Elapsed < TimeSpan.FromSeconds(3))
        {
            _ = ThreadStackCapture.Capture(id, modules, pcs);
            captures++;
        }

        stop.Cancel();
        spinner.Join();
        collector.Join();

        // Reaching this line is the test: a capture that held the thread across a collection never returned.
        Assert.True(captures > 0, $"{captures} captures in 3 s");
    }

    private static CodeRange[] Modules()
    {
        return [.. Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .Select(module => new CodeRange((ulong)module.BaseAddress, (ulong)module.BaseAddress + (ulong)module.ModuleMemorySize))];
    }

    private static string ModuleOf(ulong pc)
    {
        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
        {
            if (pc >= (ulong)module.BaseAddress && pc < (ulong)module.BaseAddress + (ulong)module.ModuleMemorySize)
            {
                return module.ModuleName.ToLowerInvariant();
            }
        }

        return $"0x{pc:x}";
    }
}
