// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.ExceptionServices;

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// Logs the first managed exceptions thrown with <c>--run</c> (first chance, caught or not) with the stack that threw
/// them. One that leaves a shim for the game's frames ends the process with 0xE0434352 and no other trace: the game's
/// handlers see it, not the runtime's, so AppDomain.UnhandledException never fires.
/// </summary>
internal static class ManagedExceptions
{
    /// <summary>Exceptions logged per process.</summary>
    private const int MaxLogged = 16;

    [ThreadStatic]
    private static bool _logging;

    private static LoaderLog? _log;
    private static int _seen;

    public static void Watch(LoaderLog log)
    {
        _log = log;
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
    }

    private static void OnFirstChance(object? sender, FirstChanceExceptionEventArgs args)
    {
        if (_logging || Interlocked.Increment(ref _seen) > MaxLogged)
        {
            return;
        }

        _logging = true;

        try
        {
            _log?.Detail($"  [clr] {args.Exception.GetType().FullName}: {args.Exception.Message}{Environment.NewLine}{Environment.StackTrace}");
        }
        finally
        {
            _logging = false;
        }
    }
}
