// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Shims.Input;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// Script input only: with an input script the game sees the scripted keys and a cursor the real mouse cannot move,
/// until F9 hands the real keyboard and mouse back (and takes them away again); --allow-real-input merges them.
/// </summary>
public sealed class ScriptedDesktopInputTests
{
    private static readonly int Space = KeyNames.Parse("Space")!.Value;
    private static readonly int Down = KeyNames.Parse("Down")!.Value;

    [Fact]
    public void Script_only_leaves_out_real_keys_and_mouse_buttons()
    {
        var real = new FakeDesktop();
        var input = Scripted(real, scriptOnly: true);
        real.Held.Add(Down);
        real.Held.Add(KeyNames.MouseLeft);

        var keys = Read(input);

        Assert.True(keys.IsDown(Space));
        Assert.False(keys.IsDown(Down));
        Assert.False(keys.IsDown(KeyNames.MouseLeft));
    }

    [Fact]
    public void Script_only_parks_the_cursor_once_and_the_real_mouse_cannot_move_it()
    {
        var real = new FakeDesktop { Cursor = new CursorPosition(1280, 720) };
        var input = Scripted(real, scriptOnly: true);

        var first = input.ReadCursor();
        var parked = input.ReadCursor();
        real.Cursor = new CursorPosition(100, 50);
        var moved = input.ReadCursor();

        Assert.Equal(new CursorPosition(1280, 720), first);
        Assert.Equal(new CursorPosition(1280 + ScriptedDesktopInput.ParkPixels, 720 - ScriptedDesktopInput.ParkPixels), parked);
        Assert.Equal(parked, moved);
    }

    [Fact]
    public void A_scripted_key_reaches_the_keyboard_and_a_pad_key_the_gamepad_never_both()
    {
        var real = new FakeDesktop();
        var script = InputScript.Parse(["0 Space hold 1000", "0 PadA hold 1000"]);
        var input = new ScriptedDesktopInput(real, script, Stopwatch.StartNew(), new LoaderLog(TextWriter.Null, null, false), scriptOnly: true);
        var pad = new PressedKeys();
        int padA = KeyNames.Parse("PadA")!.Value;

        input.ReadPadKeys(pad);
        var state = VirtualGamepad.Map(InputConfigFile.Defaults(), pad, 0, 0, 0.016f);

        Assert.True(Read(input).IsDown(Space));
        Assert.False(pad.IsDown(Space));
        Assert.True(pad.IsDown(padA));
        Assert.False(KeyNames.IsKeyboardKey(padA));
        Assert.Equal(GameInputLayout.GamepadA, state.Buttons);
    }

    [Fact]
    public void The_default_gamepad_binds_no_key_the_game_reads_from_the_keyboard()
    {
        var config = InputConfigFile.Defaults();

        Assert.DoesNotContain(config.Buttons, binding => binding.Key == Space || binding.Key == Down || binding.Key == KeyNames.Parse("Escape") || binding.Key == KeyNames.Parse("Enter"));
    }

    [Fact]
    public void F9_hands_the_real_input_back_and_takes_it_away_again()
    {
        var real = new FakeDesktop { Cursor = new CursorPosition(10, 10) };
        var input = Scripted(real, scriptOnly: true);
        _ = input.ReadCursor();

        real.Held.Add(ScriptedDesktopInput.HandBackKey);
        _ = Read(input);
        _ = Read(input);
        real.Held.Clear();
        real.Held.Add(Down);
        real.Cursor = new CursorPosition(500, 400);

        bool downHandedBack = Read(input).IsDown(Down);
        var cursorHandedBack = input.ReadCursor();
        _ = input.ReadCursor();

        real.Held.Add(ScriptedDesktopInput.HandBackKey);
        _ = Read(input);
        real.Cursor = new CursorPosition(900, 900);

        Assert.True(downHandedBack);
        Assert.Equal(new CursorPosition(500, 400), cursorHandedBack);
        Assert.True(input.ScriptOnly);
        Assert.False(Read(input).IsDown(Down));
        Assert.Equal(new CursorPosition(500 + ScriptedDesktopInput.ParkPixels, 400 - ScriptedDesktopInput.ParkPixels), input.ReadCursor());
    }

    [Fact]
    public void Allowing_real_input_merges_it_with_the_script()
    {
        var real = new FakeDesktop { Cursor = new CursorPosition(3, 4) };
        var input = Scripted(real, scriptOnly: false);
        real.Held.Add(Down);

        var keys = Read(input);

        Assert.True(keys.IsDown(Space));
        Assert.True(keys.IsDown(Down));
        Assert.Equal(new CursorPosition(3, 4), input.ReadCursor());
    }

    /// <summary>A script that holds Space from the start for a long time.</summary>
    private static ScriptedDesktopInput Scripted(FakeDesktop real, bool scriptOnly)
    {
        var script = InputScript.Parse(["0 Space hold 1000"]);

        return new ScriptedDesktopInput(real, script, Stopwatch.StartNew(), new LoaderLog(TextWriter.Null, null, false), scriptOnly);
    }

    private static PressedKeys Read(ScriptedDesktopInput input)
    {
        var keys = new PressedKeys();
        input.ReadKeys(keys);

        return keys;
    }

    private sealed class FakeDesktop : IDesktopSource
    {
        public HashSet<int> Held { get; } = [];

        public CursorPosition Cursor { get; set; }

        public void ReadKeys(PressedKeys keys)
        {
            foreach (int key in Held)
            {
                keys.Set(key, true);
            }
        }

        public CursorPosition ReadCursor()
        {
            return Cursor;
        }

        public uint ScanCode(int virtualKey)
        {
            return 0;
        }
    }
}
