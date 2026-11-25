// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Host;
using FiveStar.Loader.Shims.D3D12X.Present;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// --frame-rate: the game asks for 33333 µs frame events (30 fps at 60 Hz); 60 keeps one refresh, uncapped a 1 ms grid
/// without vertical sync, and the default keeps the game's interval.
/// </summary>
public sealed class FrameRateTests
{
    private const uint Console30 = 33333;
    private const uint Period = 3;

    [Theory]
    [InlineData("30", 33333u, 1u)]
    [InlineData("60", 16667u, 1u)]
    [InlineData("uncapped", 1000u, 0u)]
    public void The_frame_events_keep_the_chosen_interval_and_vsync(string option, uint microseconds, uint sync)
    {
        try
        {
            FramePacer.Rate = FrameRates.Parse(option);
            var pacer = new FramePacer();
            pacer.SetInterval(Console30, Period);

            Assert.Equal(microseconds, pacer.Grid.Microseconds);
            Assert.Equal(sync, FramePacer.SyncInterval);
        }
        finally
        {
            FramePacer.Rate = FrameRate.Console;
        }
    }

    [Fact]
    public void Sixty_never_slows_a_game_that_asks_for_a_shorter_interval_and_other_values_are_refused()
    {
        try
        {
            FramePacer.Rate = FrameRate.Sixty;

            Assert.Equal(8333u, FramePacer.Effective(8333));
            Assert.Throws<ArgumentException>(() => FrameRates.Parse("45"));
        }
        finally
        {
            FramePacer.Rate = FrameRate.Console;
        }
    }
}
