// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Cli.Screens;
using FiveStar.Formats.Images;
using Xunit;

namespace FiveStar.Cli.Tests;

/// <summary><c>fivestar screen learn</c>: a screen's line replaced or added in the loader's <c>--screens</c> file.</summary>
public sealed class ScreenFileTests
{
    [Fact]
    public void Learning_replaces_the_screens_line_keeping_its_limit_and_leaves_the_others()
    {
        var old = new byte[ScreenCells.Cells];
        var cells = new byte[ScreenCells.Cells];
        Array.Fill(cells, (byte)7);
        string[] lines = ["# learned", $"legal {Convert.ToBase64String(old)} 40", $"Move-Around {Convert.ToBase64String(old)} 12"];

        var learned = ScreenFile.Learn(lines, "move-around", cells, limit: null);

        Assert.Equal(lines[..2], learned.Take(2));
        Assert.Equal($"move-around {Convert.ToBase64String(cells)} 12", learned[2]);
        Assert.True(ScreenFile.TryFind(learned, "MOVE-AROUND", out var found, out int limit));
        Assert.Equal(cells, found);
        Assert.Equal(12, limit);
    }

    [Fact]
    public void A_new_screen_is_appended_with_the_given_or_the_default_limit()
    {
        var cells = new byte[ScreenCells.Cells];

        var added = ScreenFile.Learn(["terms abc"], "move-around", cells, limit: 8);
        var plain = ScreenFile.Learn([], "move-around", cells, limit: null);

        Assert.Equal(["terms abc", $"move-around {Convert.ToBase64String(cells)} 8"], added);
        Assert.EndsWith($" {ScreenCells.DefaultMaxChanged}", plain[0], StringComparison.Ordinal);
        Assert.False(ScreenFile.TryFind(added, "legal", out _, out _));
    }
}
