// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// Ends a boot from the present path once it has what it was asked for: <c>--exit-after-present N</c> after present N,
/// <c>--exit-when-done</c> when every one-shot capture fired (<see cref="CaptureCompletion"/>). The presenting thread
/// only signals <see cref="Requested"/>; the runner then ends the process as <c>--run-timeout</c> does.
/// </summary>
internal static class RunEnd
{
    private static readonly ManualResetEventSlim Signal = new(false);
    private static string? _reason;

    /// <summary>The present after which the boot ends (<c>--exit-after-present</c>); 0 for none.</summary>
    public static long AfterPresentLimit { get; set; }

    /// <summary>The captures waited for (<c>--exit-when-done</c>); null when not asked.</summary>
    public static CaptureCompletion? Captures { get; set; }

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
    }

    /// <summary>After present <paramref name="present"/>, on the presenting thread.</summary>
    public static void AfterPresent(long present)
    {
        if (Signal.IsSet || (AfterPresentLimit == 0 && Captures is null))
        {
            return;
        }

        string? reason = Captures?.AfterPresent(present);

        if (reason is null && AfterPresentLimit != 0 && present >= AfterPresentLimit)
        {
            reason = $"[exit-after-present] present {present} reached; ending";
        }

        if (reason is null)
        {
            return;
        }

        Volatile.Write(ref _reason, reason);
        Log?.Info(reason);
        Signal.Set();
    }

    /// <summary>Forgets the settings and the request (tests).</summary>
    internal static void Reset()
    {
        AfterPresentLimit = 0;
        Captures = null;
        Volatile.Write(ref _reason, null);
        Signal.Reset();
    }
}
