// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.Input;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// Input script blocks that react to the frame: <c>change</c> blocks (a press repeated until it took), regions that
/// leave out a part of the screen that moves by itself, timeouts, and the first-run script in docs.
/// </summary>
public sealed class ScriptBlockTests
{
    /// <summary>One pixel a cell at the signature's stride: each cell is the grey of its pixel.</summary>
    private const int CellPixels = 4;
    private const int Width = ScreenSignature.Columns * CellPixels;
    private const int Height = ScreenSignature.Rows * CellPixels;

    private const byte Dark = 50;
    private const byte Light = 230;

    private static readonly string[] Terms =
    [
        "0 Space repeat 100 every 3",
        "screen terms 160",
        "2 Space repeat 18 every 2.5",
        "change privacy-checked 1 region 0 5 3 13 timeout 45",
        "1 Down repeat 18 every 2.5",
    ];

    [Fact]
    public void Block_lines_take_a_limit_a_region_and_a_timeout()
    {
        var script = InputScript.Parse(Terms);

        Assert.Equal(new ScriptBlock("terms", 160, false, CellRegion.Whole, 0), script.Blocks[1]);
        Assert.Equal(new ScriptBlock("privacy-checked", 1, true, new CellRegion(0, 5, 3, 13), 45), script.Blocks[2]);
        Assert.Equal(["", "terms", "privacy-checked"], script.Screens);
        Assert.Throws<FormatException>(() => InputScript.Parse(["change boxes region 0 0 48 1"]));
        Assert.Throws<FormatException>(() => InputScript.Parse(["screen terms 160 every 3"]));
    }

    [Fact]
    public void A_press_repeats_until_the_change_it_waits_for_shows_then_stops()
    {
        var script = InputScript.Parse(Terms);
        var matcher = new BlockMatcher();
        var blank = Frame(checkedBox: false, scrolled: 0);
        var ticked = Frame(checkedBox: true, scrolled: 0);
        script.Begin(1, 100);

        // Before the first Space (102 s) the frames only set what the change is measured from.
        Assert.Null(Seen(script, matcher, blank, 101.5));
        Assert.False(script.Pressed(1, 101.5));

        // The first Space was dropped: no change, so the next one (104.5 s) is still due.
        Assert.Null(Seen(script, matcher, blank, 103));
        Assert.True(Started(script, "Space", 104.5));

        // The box shows checked in two frames in a row: the change block begins and the later Spaces are dropped.
        Assert.Null(Seen(script, matcher, ticked, 105));
        string? how = Seen(script, matcher, ticked, 105.25);
        Assert.NotNull(how);
        script.Begin(2, 105.25);

        Assert.False(Started(script, "Space", 107));
        Assert.True(Started(script, "Down", 106.25));
    }

    [Fact]
    public void Changes_outside_the_region_do_not_count()
    {
        var script = InputScript.Parse(Terms);
        var matcher = new BlockMatcher();
        script.Begin(1, 100);

        Assert.Null(Seen(script, matcher, Frame(checkedBox: false, scrolled: 0), 101));

        // The policy text scrolls by itself on the right: 120 cells change there, none in the boxes' region.
        Assert.Null(Seen(script, matcher, Frame(checkedBox: false, scrolled: 120), 103));
        Assert.Null(Seen(script, matcher, Frame(checkedBox: false, scrolled: 120), 103.25));
        Assert.Equal(0, matcher.Largest);
    }

    [Fact]
    public void A_block_times_out_after_its_wait()
    {
        var script = InputScript.Parse(Terms);
        script.Begin(1, 100);

        Assert.False(BlockMatcher.TimedOut(script.Blocks[2], script.WaitingSince(2), 144));
        Assert.True(BlockMatcher.TimedOut(script.Blocks[2], script.WaitingSince(2), 145));
        Assert.False(BlockMatcher.TimedOut(script.Blocks[1], script.WaitingSince(1), 1e6));
    }

    [Fact]
    public void A_screen_block_compares_its_region_only()
    {
        var script = InputScript.Parse(["screen boxes 0 region 0 5 3 13"]);
        var matcher = new BlockMatcher();
        var learned = ScreenSignature.Of(Frame(checkedBox: false, scrolled: 0), Width, Height);
        var scrolled = ScreenSignature.Of(Frame(checkedBox: false, scrolled: 200), Width, Height);

        Assert.Null(matcher.Seen(script.Blocks[1], 1, scrolled, learned, 0, beforePress: false, 0));
        Assert.NotNull(matcher.Seen(script.Blocks[1], 1, scrolled, learned, 0, beforePress: false, 0.25));
    }

    [Fact]
    public void A_settling_block_begins_once_its_screen_stayed_still()
    {
        var script = InputScript.Parse(["screen terms 0 settle 1 settle-region 0 0 23 26"]);
        var matcher = new BlockMatcher();
        var learned = ScreenSignature.Of(Frame(checkedBox: false, scrolled: 0), Width, Height);
        var fading = ScreenSignature.Of(Frame(checkedBox: true, scrolled: 0), Width, Height);

        // Shown at 10.25 s; the left half still changes at 10.5 s, then stays still; the right half (scrolling text) does not count.
        Assert.Null(matcher.Seen(script.Blocks[1], 1, learned, learned, 0, beforePress: false, 10));
        Assert.Null(matcher.Seen(script.Blocks[1], 1, learned, learned, 0, beforePress: false, 10.25));
        Assert.Null(matcher.Seen(script.Blocks[1], 1, fading, learned, 0, beforePress: false, 10.5));
        Assert.Null(matcher.Seen(script.Blocks[1], 1, ScreenSignature.Of(Frame(checkedBox: true, scrolled: 300), Width, Height), learned, 0, beforePress: false, 11.25));
        Assert.NotNull(matcher.Seen(script.Blocks[1], 1, fading, learned, 0, beforePress: false, 11.5));

        Assert.Equal(1.0, script.Blocks[1].Settle);
        Assert.Equal(new CellRegion(0, 0, 23, 26), script.Blocks[1].SettleRegion);
    }

    [Fact]
    public void Screens_taught_on_the_same_picture_are_named()
    {
        var script = InputScript.Parse(["screen subtitles", "screen legal", "screen terms 160"]);
        var screens = new ScreenStore(null);

        // F8 pressed twice on one screen (a focus mark apart), then on another screen: 300 cells away, past terms' 160.
        screens.Learn("subtitles", ScreenSignature.Of(Frame(checkedBox: false, scrolled: 0), Width, Height));
        screens.Learn("legal", ScreenSignature.Of(Frame(checkedBox: true, scrolled: 0), Width, Height));
        screens.Learn("terms", ScreenSignature.Of(Frame(checkedBox: false, scrolled: 300), Width, Height));

        Assert.Equal([new ScreenClash("subtitles", "legal", 2, ScreenStore.DefaultMaxChanged)], screens.Alike(script.Blocks));
    }

    [Fact]
    public void The_first_run_script_parses_with_its_terms_steps()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "first-run.input");
        var script = InputScript.Load(path);

        Assert.Equal(["", "subtitles", "legal", "legal-next", "terms", "privacy-checked", "terms-focused", "terms-checked", "confirm-focused", "story-load"], script.Screens);
        Assert.All(script.Blocks.Skip(5).Take(4), block => Assert.True(block.Change && block.Timeout > 0));
    }

    private static string? Seen(InputScript script, BlockMatcher matcher, byte[] frame, double seconds)
    {
        int block = script.WaitingBlock;
        var signature = ScreenSignature.Of(frame, Width, Height);

        return matcher.Seen(script.Blocks[block], block, signature, null, 0, !script.Pressed(block - 1, seconds), seconds);
    }

    /// <summary>Whether a press of <paramref name="key"/> is held at <paramref name="seconds"/>.</summary>
    private static bool Started(InputScript script, string key, double seconds)
    {
        var keys = new PressedKeys();
        script.Apply(keys, seconds + 0.1);

        return keys.IsDown(KeyNames.Parse(key)!.Value);
    }

    /// <summary>A terms screen: the first box at column 1, rows 6-7, dark or checked; <paramref name="scrolled"/> cells of the right panel lit.</summary>
    private static byte[] Frame(bool checkedBox, int scrolled)
    {
        const int PanelColumn = 24;
        const int PanelWidth = ScreenSignature.Columns - PanelColumn;

        var rgba = new byte[Width * Height * 4];

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int column = x / CellPixels;
                int row = y / CellPixels;
                bool box = checkedBox && column == 1 && row is 6 or 7;
                bool panel = column >= PanelColumn && ((row * PanelWidth) + column - PanelColumn) < scrolled;

                int at = ((y * Width) + x) * 4;
                rgba[at] = rgba[at + 1] = rgba[at + 2] = Grey(box || panel);
                rgba[at + 3] = byte.MaxValue;
            }
        }

        return rgba;
    }

    private static byte Grey(bool lit)
    {
        if (lit)
        {
            return Light;
        }

        return Dark;
    }
}
