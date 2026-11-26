// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// Where a thread waits inside the loader: a stack walk stops at the first JIT frame, so a game thread blocked in a shim
/// shows only the OS wait. Every wait a game thread can reach in our code registers here (<see cref="Enter"/>), and
/// <see cref="ThreadStacks"/> names each thread's wait and how long it has lasted; the totals per wait are kept too.
/// A thread's wait lives in a slot made once per thread, so entering one allocates nothing (present and storage waits
/// run every frame); a wait inside another restores the outer one when it ends.
/// </summary>
internal static class BlockingWaits
{
    private static readonly ConcurrentDictionary<uint, Slot> Slots = new();
    private static readonly ConcurrentDictionary<string, long> Totals = new(StringComparer.Ordinal);

    [ThreadStatic]
    private static Slot? _mine;

    /// <summary>Notes that this thread starts waiting in <paramref name="what"/>; dispose the result when it ends.</summary>
    public static Scope Enter(string what)
    {
        var slot = _mine ?? Register();
        var outer = new Scope(slot, slot.What, slot.Since);
        slot.Set(what, Stopwatch.GetTimestamp());

        return outer;
    }

    /// <summary>The thread's wait as "waiting in X for N ms", or empty.</summary>
    public static string Describe(uint thread)
    {
        if (!Slots.TryGetValue(thread, out var slot))
        {
            return string.Empty;
        }

        string? what = slot.What;
        long since = slot.Since;

        if (what is null)
        {
            return string.Empty;
        }

        return $"waiting in {what} for {Stopwatch.GetElapsedTime(since).TotalMilliseconds:F0} ms";
    }

    /// <summary>The wait the thread is in now, or null; allocates nothing (the CPU sampler asks at every sample).</summary>
    public static string? Current(uint thread)
    {
        if (!Slots.TryGetValue(thread, out var slot))
        {
            return null;
        }

        return slot.What;
    }

    /// <summary>The time spent in each wait so far, summed over every thread, longest first.</summary>
    public static string Summary()
    {
        return Summary("loader waits since the start, all threads", Snapshot(), new Dictionary<string, long>());
    }

    /// <summary>The totals now, in Stopwatch ticks, to measure a span from (<see cref="Summary(string, IReadOnlyDictionary{string, long}, IReadOnlyDictionary{string, long})"/>).</summary>
    public static Dictionary<string, long> Snapshot()
    {
        return new Dictionary<string, long>(Totals, StringComparer.Ordinal);
    }

    /// <summary>"<paramref name="title"/>: X 1.2 s, ..." for the waits that grew from <paramref name="before"/> to <paramref name="after"/>, longest first; "none" when none did.</summary>
    public static string Summary(string title, IReadOnlyDictionary<string, long> after, IReadOnlyDictionary<string, long> before)
    {
        var grown = after.Select(pair => (pair.Key, Ticks: pair.Value - before.GetValueOrDefault(pair.Key))).Where(pair => pair.Ticks > 0);
        var parts = grown.OrderByDescending(pair => pair.Ticks).Select(pair => $"{pair.Key} {Stopwatch.GetElapsedTime(0, pair.Ticks).TotalSeconds:F1} s").ToList();

        if (parts.Count == 0)
        {
            return $"{title}: none";
        }

        return $"{title}: {string.Join(", ", parts)}";
    }

    /// <summary>This thread's slot, made at its first wait (a thread id the OS reuses gets the new thread's slot).</summary>
    private static Slot Register()
    {
        var slot = new Slot();
        Slots[Kernel32.GetCurrentThreadId()] = slot;
        _mine = slot;

        return slot;
    }

    /// <summary>A thread's current wait: what (null for none) and since when; the hang watch reads it from another thread.</summary>
    internal sealed class Slot
    {
        private string? _what;
        private long _since;

        public string? What => Volatile.Read(ref _what);

        public long Since => Volatile.Read(ref _since);

        public void Set(string? what, long since)
        {
            Volatile.Write(ref _since, since);
            Volatile.Write(ref _what, what);
        }
    }

    /// <summary>Ends a wait noted by <see cref="Enter"/>, restoring the wait it was inside of, if any.</summary>
    internal readonly struct Scope : IDisposable
    {
        private readonly Slot _slot;
        private readonly string? _outer;
        private readonly long _outerSince;

        public Scope(Slot slot, string? outer, long outerSince)
        {
            _slot = slot;
            _outer = outer;
            _outerSince = outerSince;
        }

        public void Dispose()
        {
            string? what = _slot.What;
            long took = Stopwatch.GetTimestamp() - _slot.Since;
            _slot.Set(_outer, _outerSince);

            if (what is not null)
            {
                Totals.AddOrUpdate(what, static (_, ticks) => ticks, static (_, total, ticks) => total + ticks, took);
            }
        }
    }
}
