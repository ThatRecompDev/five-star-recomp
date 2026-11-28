// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Host;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// --exit-when-done and --exit-after-present: a run ends a grace after its last requested capture is written, or, when
/// one never fires, a fixed number of presents after the last scheduled target, naming what never fired.
/// </summary>
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
