// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Host;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using FiveStar.Loader.Shims.D3D12X.Present;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// --exit-when-done and --exit-after-present: a run ends a grace after its last requested capture is written, or, when
/// one never fires, a fixed number of presents after the last scheduled target, naming what never fired.
/// </summary>
[Collection(Support.RunEndState.Name)]
public sealed class RunEndTests
{
    private const int Grace = 30;
    private const long Fallback = 600;

    [Fact]
    public void The_run_ends_the_grace_after_the_last_capture_is_written()
    {
        var captures = new CaptureCompletion(Grace, Fallback);
        captures.Expect("capture-presents 1", 1);
        captures.Expect("capture-presents 1500", 1500);
        captures.Expect("capture-dispatch CS_Foo@2", 0);

        captures.Fired("capture-presents 1");
        captures.Fired("capture-dispatch CS_Foo@2");
        Assert.Null(captures.AfterPresent(1499));

        captures.Fired("capture-presents 1500");
        Assert.Null(captures.AfterPresent(1500));
        Assert.Null(captures.AfterPresent(1529));
        Assert.Equal("[exit-when-done] all requested captures done at present 1500; ending", captures.AfterPresent(1530));
    }

    [Fact]
    public void An_entry_that_never_fires_is_given_up_after_the_last_scheduled_present_and_named()
    {
        var captures = new CaptureCompletion(Grace, Fallback);
        captures.Expect("capture-presents 900", 900);
        captures.Expect("capture-dispatch CS_Never@p100:1", 100);
        captures.Fired("capture-presents 900");

        Assert.Null(captures.AfterPresent(1499));
        string? line = captures.AfterPresent(1500);

        Assert.NotNull(line);
        Assert.Contains("never fired: capture-dispatch CS_Never@p100:1", line, StringComparison.Ordinal);
        Assert.Equal(["capture-dispatch CS_Never@p100:1"], captures.Pending());
    }

    [Fact]
    public void A_capture_scheduled_later_holds_the_end_and_moves_the_fallback()
    {
        var captures = new CaptureCompletion(Grace, Fallback);
        captures.Expect("capture-when-world", 0);
        captures.Expect("dump-frame", 0);

        // Nothing scheduled yet: no fallback, so the run goes on.
        Assert.Null(captures.AfterPresent(5000));

        // The world trigger schedules its captures and fires itself; the dump learns its present.
        captures.Expect("capture-presents 5101", 5101);
        captures.Fired("capture-when-world");
        captures.Schedule("dump-frame", 5300);
        captures.Fired("capture-presents 5101");
        Assert.Null(captures.AfterPresent(5899));
        Assert.Contains("never fired: dump-frame", captures.AfterPresent(5900), StringComparison.Ordinal);
    }

    [Fact]
    public void A_capture_expected_after_all_were_done_starts_the_grace_again()
    {
        var captures = new CaptureCompletion(Grace, Fallback);
        captures.Expect("capture-presents 10", 10);
        captures.Fired("capture-presents 10");
        Assert.Null(captures.AfterPresent(10));

        captures.Expect("capture-presents 20", 20);
        Assert.Null(captures.AfterPresent(40));
        captures.Fired("capture-presents 20");
        Assert.Null(captures.AfterPresent(41));
        Assert.NotNull(captures.AfterPresent(71));
    }

    [Fact]
    public void Exit_after_present_and_exit_when_done_signal_the_runner()
    {
        try
        {
            RunEnd.Reset();
            RunEnd.AfterPresentLimit = 100;
            RunEnd.AfterPresent(99);
            Assert.False(RunEnd.IsRequested);

            RunEnd.AfterPresent(100);
            Assert.True(RunEnd.IsRequested);
            Assert.Equal("[exit-after-present] present 100 reached; ending", RunEnd.Reason);

            RunEnd.Reset();
            RunEnd.Captures = new CaptureCompletion(0, Fallback);
            RunEnd.Expect("pix-capture 5", 5);
            RunEnd.AfterPresent(5);
            Assert.False(RunEnd.IsRequested);

            RunEnd.Fired("pix-capture 5");
            RunEnd.AfterPresent(6);
            Assert.True(RunEnd.IsRequested);
        }
        finally
        {
            RunEnd.Reset();
        }
    }

    [Fact]
    public void A_script_exit_waits_for_the_lines_it_started_but_not_for_later_ones()
    {
        var work = new ScriptWork(Fallback);
        work.Expect("input-script capture 1:2");
        work.Expect("input-script capture 1:9");
        work.Started("input-script capture 1:2");
        work.Started("input-script dump 1:3");

        Assert.Null(work.Due(400));
        work.Exit(125.5);
        Assert.Null(work.Due(400));

        work.Fired("input-script capture 1:2");
        work.Fired("capture-presents 401");
        Assert.Null(work.Due(401));

        work.Fired("input-script dump 1:3");
        Assert.Equal("[input-script] exit line at 125.5 s, every capture and dump started before it written; ending", work.Due(402));
    }

    [Fact]
    public void A_script_exit_gives_up_a_line_that_never_writes_and_names_it()
    {
        var work = new ScriptWork(Fallback);
        work.Started("input-script dump 2:1");
        work.Exit(80);

        Assert.Null(work.Due(1000));
        Assert.Null(work.Due(1599));
        Assert.Contains("never written: input-script dump 2:1", work.Due(1600), StringComparison.Ordinal);
    }

    [Fact]
    public void A_withdrawn_script_line_is_forgotten_unless_it_started()
    {
        var work = new ScriptWork(Fallback);
        work.Expect("input-script capture 0:5");
        work.Started("input-script dump 0:6");

        Assert.True(work.Withdraw("input-script capture 0:5"));
        Assert.False(work.Withdraw("input-script dump 0:6"));
        Assert.Equal([("input-script dump 0:6", true)], work.Open());
    }

    [Fact]
    public void Script_lines_not_due_yet_hold_the_exit_when_done_fallback()
    {
        var captures = new CaptureCompletion(Grace, Fallback);
        captures.Expect("capture-presents 100", 100);
        captures.Expect("input-script capture 1:2", CaptureCompletion.Held);
        captures.Fired("capture-presents 100");

        // The screen block has not begun: no fallback, however long the load takes.
        Assert.Null(captures.AfterPresent(5000));

        captures.Schedule("input-script capture 1:2", 5001);
        captures.Fired("input-script capture 1:2");
        Assert.Null(captures.AfterPresent(5001));
        Assert.Equal("[exit-when-done] all requested captures done at present 5001; ending", captures.AfterPresent(5031));
    }

    [Fact]
    public void Exit_when_done_counts_the_script_lines_and_the_exit_line_ends_the_run()
    {
        try
        {
            // The watcher expects its lines before the run arms --exit-when-done.
            RunEnd.Reset();
            RunEnd.ExpectScript("input-script capture 1:2");
            RunEnd.ExpectScript("input-script dump 1:3");

            var captures = new CaptureCompletion(0, Fallback);
            RunEnd.Captures = captures;
            RunEnd.ExpectScriptWork(captures);
            Assert.Equal(["input-script capture 1:2", "input-script dump 1:3"], captures.Names);

            RunEnd.AfterPresent(700);
            RunEnd.StartScript("input-script capture 1:2");
            RunEnd.Fired("input-script capture 1:2");
            RunEnd.AfterPresent(701);
            Assert.False(RunEnd.IsRequested);

            RunEnd.StartScript("input-script dump 1:3");
            RunEnd.Fired("input-script dump 1:3");
            RunEnd.AfterPresent(720);
            Assert.True(RunEnd.IsRequested);
            Assert.StartsWith("[exit-when-done]", RunEnd.Reason, StringComparison.Ordinal);

            // Without --exit-when-done, an exit line with nothing started ends the run at once.
            RunEnd.Reset();
            RunEnd.ExpectScript("input-script capture 1:9");
            RunEnd.ExitScript(42);
            Assert.True(RunEnd.IsRequested);
            Assert.StartsWith("[input-script] exit line at 42.0 s", RunEnd.Reason, StringComparison.Ordinal);
        }
        finally
        {
            RunEnd.Reset();
        }
    }

    [Fact]
    public void A_script_exit_waits_for_a_started_capture_to_be_written()
    {
        try
        {
            RunEnd.Reset();
            RunEnd.AfterPresent(300);
            RunEnd.StartScript("input-script capture 1:2");
            RunEnd.ExitScript(10);
            RunEnd.AfterPresent(301);
            Assert.False(RunEnd.IsRequested);

            RunEnd.Fired("input-script capture 1:2");
            RunEnd.AfterPresent(302);
            Assert.True(RunEnd.IsRequested);
        }
        finally
        {
            RunEnd.Reset();
        }
    }

    [Fact]
    public void Every_present_trigger_counts_the_same_presents()
    {
        try
        {
            RunEnd.Reset();
            RunEnd.AfterPresentLimit = 850;
            using var capture = new FrameCapture(null, [800], new LoaderLog(TextWriter.Null, null, false));
            var dump = new FrameStageDump();
            FrameStageDump.Present = 800;
            FrameStageDump.Directory = Path.Combine(Path.GetTempPath(), "fivestar-present-count-test");

            var captured = new List<long>();
            long opened = 0;
            long dumpAsked = 0;

            // As the presenter numbers them: a frame skipped while minimized is no present for any of them.
            for (long present = 1; present <= 900 && !RunEnd.IsRequested; present++)
            {
                if (present == 810)
                {
                    RunEnd.StartScript("input-script capture 1:2");
                    capture.RequestNext("input-script capture 1:2");
                    FrameStageDump.RequestNext("input-script dump 1:2");
                }

                RunEnd.AfterPresent(present);

                if (capture.Wants(present))
                {
                    captured.Add(present);
                    capture.Written(present);
                }

                if (dump.Begin(present, reportPending: false, written: 0) is not null && dump.IsOpen && opened == 0)
                {
                    opened = present;
                }

                if (dumpAsked == 0 && dump.TakeAsked())
                {
                    dumpAsked = present;
                }
            }

            Assert.Equal([800, 810], captured);
            Assert.Equal(800, opened);
            Assert.Equal(810, dumpAsked);
            Assert.Empty(RunEnd.Script.Open());
            Assert.Equal("[exit-after-present] present 850 reached; ending", RunEnd.Reason);
        }
        finally
        {
            FrameStageDump.Present = 0;
            RunEnd.Reset();
        }
    }

    [Fact]
    public void The_options_parse_with_and_without_a_grace()
    {
        var plain = new CommandLineParser(["--run", "--exit-when-done", "--exit-after-present", "2000"]).Parse();
        var given = new CommandLineParser(["--exit-when-done", "5", "--run"]).Parse();

        Assert.Equal(CaptureCompletion.DefaultGrace, plain.ExitWhenDone);
        Assert.Equal(2000, plain.ExitAfterPresent);
        Assert.Equal(5, given.ExitWhenDone);
        Assert.True(given.RunMain);
    }
}
