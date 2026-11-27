// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// Ends the process once the desktop device is removed. After w80's removal the game went on allocating and recording
/// against a dead device until free memory fell below 4 GB and commit reached about 106 GB (the boot guard ended it);
/// on a 16 GB machine that is a swap storm, not a crash. The first sign of the removal (a queue fence reading
/// UINT64_MAX, a present failing with a removal code, the debug layer's removal event) is logged, new submissions are
/// dropped (<see cref="Lost"/>), and after <see cref="GraceSeconds"/> s, which leaves the removal reports (queues,
/// breadcrumbs, DRED) time to be written, the process ends with <see cref="ExitCode"/>.
/// </summary>
internal static class DeviceLoss
{
    /// <summary>The process's exit code after a device removal.</summary>
    public const int ExitCode = 6;

    /// <summary>Seconds between the first sign of the removal and the end of the process.</summary>
    public const double GraceSeconds = 3;

    /// <summary>DXGI_ERROR_DEVICE_HUNG, _REMOVED, _RESET and _DRIVER_INTERNAL_ERROR: what a present returns after a removal.</summary>
    private const int Hung = unchecked((int)0x887A0006);
    private const int Removed = unchecked((int)0x887A0005);
    private const int Reset = unchecked((int)0x887A0007);
    private const int DriverInternal = unchecked((int)0x887A0020);

    private const int MillisecondsPerSecond = 1000;

    private static int _lost;

    /// <summary>Whether the device was removed: new GPU work is dropped.</summary>
    public static bool Lost => Volatile.Read(ref _lost) != 0;

    /// <summary>Whether a removal ends the process: set by the loader's run (tests and tools that make devices are not ended).</summary>
    public static bool Armed { get; set; }

    /// <summary>Where the end is logged; null logs nothing.</summary>
    public static LoaderLog? Log { get; set; }

    /// <summary>
    /// Runs at once on the device-loss thread with the sign, before the grace period is waited out: reports written
    /// elsewhere (an Aftermath crash dump), which may take longer than the grace; null for none.
    /// </summary>
    public static Action<string>? BeforeEnd { get; set; }

    /// <summary>Ends the process with an exit code (tests replace it).</summary>
    public static Action<int> End { get; set; } = code =>
    {
        Console.Out.Flush();
        _ = Kernel32.TerminateProcess(Kernel32.GetCurrentProcess(), (uint)code);
    };

    /// <summary>Whether an HRESULT says the device is gone.</summary>
    public static bool IsRemoval(int hr)
    {
        return hr is Hung or Removed or Reset or DriverInternal;
    }

    /// <summary>The device was removed, as <paramref name="sign"/> shows: logged and the end scheduled, once.</summary>
    public static void Noticed(string sign)
    {
        if (!Armed || Interlocked.Exchange(ref _lost, 1) != 0)
        {
            return;
        }

        Log?.Error($"[device lost] {sign}: new GPU work is dropped and the process ends in {GraceSeconds:F0} s (exit code {ExitCode}); {MemorySnapshot.Summary()}");
        var thread = new Thread(() => Finish(sign)) { IsBackground = true, Name = "fivestar device loss" };
        thread.Start();
    }

    /// <summary>Forgets a removal (tests).</summary>
    public static void Clear()
    {
        Volatile.Write(ref _lost, 0);
    }

    private static void Finish(string sign)
    {
        var started = Stopwatch.StartNew();
        KernelGraphicsCalls.LogDeviceStates(sign);
        BeforeEnd?.Invoke(sign);
        int left = (int)((GraceSeconds * MillisecondsPerSecond) - started.ElapsedMilliseconds);

        if (left > 0)
        {
            Thread.Sleep(left);
        }

        Log?.Error($"[device lost] ending the process (exit code {ExitCode}); {MemorySnapshot.Summary()}");
        End(ExitCode);
    }
}
