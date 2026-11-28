// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// Ends a boot once it has what it was asked for: <c>--exit-after-present N</c> after present N,
/// <c>--exit-when-done</c> when every one-shot capture fired (<see cref="CaptureCompletion"/>, the input script's capture
/// and dump lines among them), an input script's <c>exit</c> line once the lines it started are written
/// (<see cref="ScriptWork"/>). The present path and the script only signal <see cref="Requested"/>; the runner then ends
/// the process as <c>--run-timeout</c> does.
/// </summary>
internal static class RunEnd
{
    private static readonly ManualResetEventSlim Signal = new(false);
    private static string? _reason;
    private static long _lastPresent;

    /// <summary>The present after which the boot ends (<c>--exit-after-present</c>); 0 for none.</summary>
    public static long AfterPresentLimit { get; set; }

    /// <summary>The captures waited for (<c>--exit-when-done</c>); null when not asked.</summary>
    public static CaptureCompletion? Captures { get; set; }

    /// <summary>The input script's capture and dump lines not written yet, and its exit line.</summary>
    public static ScriptWork Script { get; private set; } = new(CaptureCompletion.DefaultFallbackPresents);

    /// <summary>Where the ending line goes.</summary>
    public static LoaderLog? Log { get; set; }

    /// <summary>Set once the boot should end.</summary>
    public static WaitHandle Requested => Signal.WaitHandle;

    /// <summary>Whether the boot should end now.</summary>
    public static bool IsRequested => Signal.IsSet;

    /// <summary>Why the boot ends; null before.</summary>
    public static string? Reason => Volatile.Read(ref _reason);

    /// <summary>Expects a capture (<see cref="CaptureCompletion.Expect"/>); nothing without <c>--exit-when-done</c>.</summary>
    public static void Expect(string name, long present)
    {
        Captures?.Expect(name, present);
    }

    /// <summary>The present a capture is now scheduled for (<see cref="CaptureCompletion.Schedule"/>).</summary>
    public static void Schedule(string name, long present)
    {
        Captures?.Schedule(name, present);
    }

    /// <summary>A capture is written (<see cref="CaptureCompletion.Fired"/>); any thread.</summary>
    public static void Fired(string name)
    {
        Captures?.Fired(name);
        Script.Fired(name);
    }

    /// <summary>A script capture or dump line whose time has not come; <c>--exit-when-done</c> waits for it.</summary>
    public static void ExpectScript(string name)
    {
        Script.Expect(name);
        Captures?.Expect(name, CaptureCompletion.Held);
    }

    /// <summary>A reload dropped an expected script line.</summary>
    public static void WithdrawScript(string name)
    {
        if (Script.Withdraw(name))
        {
            Captures?.Withdraw(name);
        }
    }

    /// <summary>A script capture or dump line asked for the next present.</summary>
    public static void StartScript(string name)
    {
        Script.Started(name);
        Captures?.Expect(name, CaptureCompletion.Held);
        Captures?.Schedule(name, Volatile.Read(ref _lastPresent) + 1);
    }

    /// <summary>The script's exit line came at <paramref name="seconds"/>: the boot ends once the lines it started are written.</summary>
    public static void ExitScript(double seconds)
    {
        Script.Exit(seconds);
        End(Script.Due(Volatile.Read(ref _lastPresent)));
    }

    /// <summary>
    /// <c>--exit-when-done</c>: expects the script lines not written yet, those started scheduled at the next present
    /// (the script runs before <see cref="Captures"/> is set).
    /// </summary>
    public static void ExpectScriptWork(CaptureCompletion captures)
    {
        foreach (var (name, started) in Script.Open())
        {
            captures.Expect(name, CaptureCompletion.Held);

            if (started)
            {
                captures.Schedule(name, Volatile.Read(ref _lastPresent) + 1);
            }
        }
    }

    /// <summary>After present <paramref name="present"/>, on the presenting thread.</summary>
    public static void AfterPresent(long present)
    {
        Volatile.Write(ref _lastPresent, present);

        if (Signal.IsSet)
        {
            return;
        }

        string? reason = Captures?.AfterPresent(present);

        if (reason is null && AfterPresentLimit != 0 && present >= AfterPresentLimit)
        {
            reason = $"[exit-after-present] present {present} reached; ending";
        }

        End(reason ?? Script.Due(present));
    }

    /// <summary>Forgets the settings and the request (tests).</summary>
    internal static void Reset()
    {
        AfterPresentLimit = 0;
        Captures = null;
        Script = new ScriptWork(CaptureCompletion.DefaultFallbackPresents);
        Volatile.Write(ref _lastPresent, 0);
        Volatile.Write(ref _reason, null);
        Signal.Reset();
    }

    /// <summary>Logs <paramref name="reason"/> and signals the runner, once; null does nothing.</summary>
    private static void End(string? reason)
    {
        if (reason is null || Interlocked.CompareExchange(ref _reason, reason, null) is not null)
        {
            return;
        }

        Log?.Info(reason);
        Signal.Set();
    }
}
