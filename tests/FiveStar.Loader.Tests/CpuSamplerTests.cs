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
