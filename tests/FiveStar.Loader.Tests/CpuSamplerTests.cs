// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Codegen;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Loading;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// --cpu-sample: a stack is placed in the game, our code, a system DLL or a wait by the first game and managed frames
/// on it, and system code is named by its export.
/// </summary>
public sealed class CpuSamplerTests
{
    private const ulong Game = 0x1000_0000;
    private const ulong GameCaller = 0x1000_2345;
    private const ulong Native = 0x7000_0000;
    private const ulong Wait = 0x7000_0010;
    private const ulong Managed = 0x9000_0000;
    private const ulong Site = 0x1000_4444;

    private static readonly CpuAddressMap Map = new(
        [new CodeRange(Game, Game + 0x10_0000)],
        [new CodeRange(Native, Native + 0x10_0000)],
        [new CodeRange(Wait, Wait + 0x20)],
        pc => pc & ~0xFFFUL);

    [Fact]
    public void Running_code_is_placed_by_its_first_game_and_managed_frames()
    {
        Assert.Equal(new CpuSample(CpuBucket.Game, 0, Game + 0x2000, 0, null), Map.Classify([GameCaller + 0x100, Native], Site, "ignored"));
        Assert.Equal(new CpuSample(CpuBucket.System, Native + 0x500, Game + 0x2000, 0, null), Map.Classify([Native + 0x500, GameCaller], 0, null));
        Assert.Equal(new CpuSample(CpuBucket.Ours, 0, 0, Site, "storage read", true), Map.Classify([Managed], Site, "storage read"));
        Assert.Equal(new CpuSample(CpuBucket.Ours, Native + 0x500, 0, Site, null, true), Map.Classify([Native + 0x500, Managed], Site, null));
        Assert.Equal(new CpuSample(CpuBucket.Ours, 0, 0, GameCaller, null, true), Map.Classify([Managed, GameCaller], 0, null));
        Assert.Equal(CpuBucket.Unread, Map.Classify([], Site, null).Bucket);
    }

    [Fact]
    public void A_wait_stub_innermost_is_blocked_in_the_game_or_in_our_code()
    {
        Assert.Equal(new CpuSample(CpuBucket.Blocked, Wait, Game + 0x2000, 0, null), Map.Classify([Wait, Native + 0x500, GameCaller], Site, null));
        Assert.Equal(new CpuSample(CpuBucket.Blocked, Wait, 0, Site, "present recorder fence", true), Map.Classify([Wait, Native + 0x500, Managed], Site, "present recorder fence"));
    }

    [Fact]
    public void The_option_takes_an_interval_and_a_report_period()
    {
        Assert.True(CpuSampler.TryParse("2:5", out var interval, out var report));
        Assert.Equal((TimeSpan.FromMilliseconds(2), TimeSpan.FromSeconds(5)), (interval, report));
        Assert.True(CpuSampler.TryParse("1.5", out _, out report));
        Assert.Equal(TimeSpan.FromSeconds(10), report);
        Assert.False(CpuSampler.TryParse("0", out _, out _));
        Assert.False(CpuSampler.TryParse("2:x", out _, out _));
    }

    /// <summary>A window over a sleeping "mapped main" thread finds it blocked in NtDelayExecution under our code.</summary>
    [Fact]
    public void A_window_reports_the_main_thread_blocked_in_its_sleep()
    {
        using var started = new ManualResetEventSlim();
        var sleeper = new Thread(() =>
        {
            started.Set();
            Thread.Sleep(Timeout.Infinite);
        }) { IsBackground = true, Name = "mapped main" };

        sleeper.Start();
        started.Wait();
        Thread.Sleep(50);

        var output = new StringWriter();
        var sampler = new CpuSampler(new ImageTable(), new CallTrace(new CodeHeap()), new LoaderLog(output, null, false));
        sampler.Window(1, TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(200));
        string log = output.ToString();

        Assert.True(log.Contains("all game threads: game 0.0 %, ours 0.0 %, system 0.0 %, blocked 100.0 %", StringComparison.Ordinal), log);
        Assert.True(log.Contains("blocked: NtDelayExecution in ours: no game caller 100.0 %", StringComparison.Ordinal), log);

        // The thread does not run while it sleeps: after its first snapshot its sample is counted again without one.
        var reused = System.Text.RegularExpressions.Regex.Match(log, @"(\d+) samples counted again without a snapshot");
        Assert.True(reused.Success && int.Parse(reused.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) > 0, log);
    }

    /// <summary>The sampler's walk goes on through JIT frames: a managed thread's stack reaches its start in kernel32.</summary>
    [Fact]
    public void The_sampler_walk_goes_through_managed_frames_to_the_thread_start()
    {
        uint id = 0;
        using var started = new ManualResetEventSlim();
        var sleeper = new Thread(() =>
        {
            id = FiveStar.Loader.Native.Kernel32.GetCurrentThreadId();
            started.Set();
            Thread.Sleep(Timeout.Infinite);
        }) { IsBackground = true };

        sleeper.Start();
        started.Wait();
        Thread.Sleep(50);

        var modules = new ThreadStacks(new ImageTable(), 1).Modules();
        var pcs = new ulong[256];
        int count = ThreadStackCapture.Capture(id, modules, pcs, 1 << 20, _ => false, _ => false, out _);
        var names = pcs.Take(count).Select(pc => FiveStar.Loader.Native.SystemModules.Describe((nint)pc)).ToList();

        Assert.Contains(names, name => name.StartsWith("KERNEL32.DLL", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A managed method on another thread's stack is named from the table of the assembly's methods by their code.</summary>
    [Fact]
    public unsafe void A_running_managed_method_is_named_by_its_code()
    {
        uint id = 0;
        _spinning = true;
        using var started = new ManualResetEventSlim();
        var spinner = new Thread(() =>
        {
            id = FiveStar.Loader.Native.Kernel32.GetCurrentThreadId();
            started.Set();
            Spin();
        }) { IsBackground = true };

        spinner.Start();
        started.Wait();
        Thread.Sleep(50);

        try
        {
            var names = new ManagedNames(typeof(CpuSamplerTests).Assembly);
            names.Refresh();
            var modules = new ThreadStacks(new ImageTable(), 1).Modules();
            var pcs = new ulong[256];
            int count = ThreadStackCapture.Capture(id, modules, pcs, 1 << 20, _ => false, _ => false, out _);
            var found = pcs.Take(count).Select(pc => names.Find(Start(pc))).Where(name => name is not null).ToList();

            Assert.Contains("CpuSamplerTests.Spin", found);
        }
        finally
        {
            _spinning = false;
            spinner.Join();
        }
    }

    private static volatile bool _spinning;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining | System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static void Spin()
    {
        while (_spinning)
        {
            Thread.SpinWait(1);
        }
    }

    private static unsafe ulong Start(ulong pc)
    {
        ulong imageBase = 0;
        nint function = FiveStar.Loader.Native.Ntdll.RtlLookupFunctionEntry(pc, &imageBase, 0);

        if (function == 0)
        {
            return 0;
        }

        return imageBase + *(uint*)function;
    }

    [Fact]
    public void System_code_is_named_by_the_export_its_function_starts_at()
    {
        nint ntdll = NativeLibrary.Load("ntdll.dll");
        ulong allocate = (ulong)NativeLibrary.GetExport(ntdll, "RtlAllocateHeap");
        var names = new ExportNames();

        Assert.Equal("ntdll.dll!RtlAllocateHeap", names.Describe(allocate), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("ntdll.dll!RtlAllocateHeap", names.Describe(allocate + 0x8), StringComparer.OrdinalIgnoreCase);
    }
}
