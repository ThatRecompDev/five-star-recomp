// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using FiveStar.Loader.Diagnostics;

namespace FiveStar.Loader.Shims.Input;

/// <summary>
/// A desktop source with the presses of an <see cref="InputScript"/> added to the keys held for real. The script can
/// be replaced while the game runs (<see cref="Replace"/>); presses of the new script that already started are not
/// announced again. Two threads poll it (keyboard and gamepad readings), so each press is claimed once.
/// <para>
/// Script only (the default with a script, <c>--allow-real-input</c> turns it off): the game sees the scripted keys
/// alone, real keys and mouse buttons left out, and a cursor that stays where the real one was when the script took
/// over, moved only by the script's <c>cursor</c> lines, so a hand on the mouse cannot move a menu's focus.
/// <see cref="HandBackKey"/> (F9) toggles the real keyboard and mouse back in and out; the teach key reads them anyway.
/// </para>
/// </summary>
internal sealed class ScriptedDesktopInput : IDesktopSource
{
    /// <summary>F9 on the real keyboard.</summary>
    public const int HandBackKey = 0x78;

    private readonly IDesktopSource _inner;
    private readonly Stopwatch _clock;
    private readonly LoaderLog _log;
    private volatile ScriptState _state;
    private int _scriptOnly;
    private int _handBackHeld;
    private int _originSet;
    private long _originX;
    private long _originY;

    /// <param name="inner">The real keyboard and mouse.</param>
    /// <param name="script">The presses.</param>
    /// <param name="clock">Started when the loader started; the script's times count from it.</param>
    /// <param name="log">Each press is logged when it starts.</param>
    /// <param name="scriptOnly">Whether the game sees the script's input alone until F9 is pressed.</param>
    public ScriptedDesktopInput(IDesktopSource inner, InputScript script, Stopwatch clock, LoaderLog log, bool scriptOnly)
    {
        _inner = inner;
        _clock = clock;
        _log = log;
        _state = new ScriptState(script, new int[script.Presses.Count]);
        _scriptOnly = Flag(scriptOnly);
    }

    /// <summary>Whether the real keyboard and mouse are left out now.</summary>
    public bool ScriptOnly => Volatile.Read(ref _scriptOnly) != 0;

    /// <summary>Uses <paramref name="script"/> from the next poll on; any thread.</summary>
    public void Replace(InputScript script)
    {
        double seconds = _clock.Elapsed.TotalSeconds;
        var announced = new int[script.Presses.Count];

        for (int i = 0; i < announced.Length; i++)
        {
            if (script.Started(i, seconds))
            {
                announced[i] = 1;
            }
        }

        _state = new ScriptState(script, announced);
    }

    [HotPath]
    public void ReadKeys(PressedKeys keys)
    {
        _inner.ReadKeys(keys);
        HandBack(keys.IsDown(HandBackKey));

        if (ScriptOnly)
        {
            keys.Clear();
        }

        double seconds = _clock.Elapsed.TotalSeconds;
        var state = _state;
        state.Script.Apply(keys, seconds);
        Announce(state, seconds);
    }

    /// <summary>Fills <paramref name="keys"/> with the keys held for real, without the script's.</summary>
    public void ReadRealKeys(PressedKeys keys)
    {
        _inner.ReadKeys(keys);
    }

    /// <summary>The real cursor plus the script's offset; script only, the origin it had when the script took over plus the offset.</summary>
    [HotPath]
    public CursorPosition ReadCursor()
    {
        var cursor = _inner.ReadCursor();
        var offset = _state.Script.CursorOffset(_clock.Elapsed.TotalSeconds);

        if (!ScriptOnly || Interlocked.Exchange(ref _originSet, 1) == 0)
        {
            Volatile.Write(ref _originX, cursor.X);
            Volatile.Write(ref _originY, cursor.Y);
        }

        if (ScriptOnly)
        {
            cursor = new CursorPosition(Volatile.Read(ref _originX), Volatile.Read(ref _originY));
        }

        return new CursorPosition(cursor.X + offset.X, cursor.Y + offset.Y);
    }

    [HotPath]
    public uint ScanCode(int virtualKey)
    {
        return _inner.ScanCode(virtualKey);
    }

    [HotPath]
    private void Announce(ScriptState state, double seconds)
    {
        for (int i = 0; i < state.Announced.Length; i++)
        {
            if (state.Announced[i] == 0 && state.Script.Started(i, seconds) && Interlocked.Exchange(ref state.Announced[i], 1) == 0)
            {
                Log(state.Script.Presses[i], seconds);
            }
        }
    }

    private static int Flag(bool on)
    {
        if (on)
        {
            return 1;
        }

        return 0;
    }

    /// <summary>F9 went down: the real keyboard and mouse are toggled in or out. Two pollers see it once.</summary>
    [HotPath]
    private void HandBack(bool held)
    {
        if (Interlocked.Exchange(ref _handBackHeld, Flag(held)) != 0 || !held)
        {
            return;
        }

        int scriptOnly = 1 - Volatile.Read(ref _scriptOnly);
        Volatile.Write(ref _scriptOnly, scriptOnly);
        Toggled(scriptOnly != 0);
    }

    private void Toggled(bool scriptOnly)
    {
        if (scriptOnly)
        {
            _log.Info("  [input] F9: the game sees the script's input only again");

            return;
        }

        _log.Info("  [input] F9: real keyboard and mouse handed back to the game (F9 again takes them away)");
    }

    private void Log(ScriptedPress press, double seconds)
    {
        _log.Info($"  [input] script: {press.Name} down at {seconds:F1} s for {press.Hold} s");
    }
}
