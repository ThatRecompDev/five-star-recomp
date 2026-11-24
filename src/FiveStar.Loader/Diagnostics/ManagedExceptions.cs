// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.ExceptionServices;

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// Logs the first managed exception of each type thrown with <c>--run</c> (first chance, caught or not) with the stack
/// that threw it: the translator's own, caught ones would fill the log otherwise. One that leaves a shim for the game's frames ends the process with 0xE0434352 and no other trace: the game's
/// handlers see it, not the runtime's, so AppDomain.UnhandledException never fires.
/// </summary>
internal static class ManagedExceptions
{
    /// <summary>Exception types logged per process.</summary>
    private const int MaxLogged = 16;

    private static readonly HashSet<Type> Seen = [];

    [ThreadStatic]
    private static bool _logging;

    private static LoaderLog? _log;

    public static void Watch(LoaderLog log)
    {
        _log = log;
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
    }

    private static bool First(Type type)
    {
        lock (Seen)
        {
            return Seen.Count < MaxLogged && Seen.Add(type);
        }
    }

    private static void OnFirstChance(object? sender, FirstChanceExceptionEventArgs args)
    {
        if (_logging || !First(args.Exception.GetType()))
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
