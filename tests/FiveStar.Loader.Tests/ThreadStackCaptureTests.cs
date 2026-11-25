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

    [Fact]
    public void The_calling_thread_is_not_read_and_does_not_suspend_itself()
    {
        var modules = Modules();
        int count = -1;
        bool found = true;
        var self = new Thread(() =>
        {
            uint id = Kernel32.GetCurrentThreadId();
            count = ThreadStackCapture.Capture(id, modules, new ulong[Frames]);
            found = ThreadStackCapture.FrameRegisters(id, modules, new CodeRange(0, ulong.MaxValue), out _, out _);
        }) { IsBackground = true };

        self.Start();

        // A thread that suspended itself never returns, and it kept the gate every other capture waits on.
        Assert.True(self.Join(TimeSpan.FromSeconds(10)), "the capture of the calling thread did not return");
        Assert.Equal(0, count);
        Assert.False(found);
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
