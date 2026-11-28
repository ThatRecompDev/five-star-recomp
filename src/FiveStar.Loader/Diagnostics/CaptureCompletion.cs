// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// The one-shot captures and dumps a boot was asked for (<c>--exit-when-done</c>), and when the boot may end: once every
/// entry has fired and <see cref="Grace"/> more presents went by, or, when some never fire, <see cref="FallbackPresents"/>
/// presents after the last present any entry was scheduled for (none scheduled: no fallback). Entries can be added
/// while the boot runs (the world trigger schedules its captures then); a name fired before it is expected is ignored.
/// </summary>
internal sealed class CaptureCompletion
{
    /// <summary>Presents waited after the last capture by default.</summary>
    public const int DefaultGrace = 30;

    /// <summary>Presents after the last scheduled target at which a boot whose entries did not all fire ends anyway.</summary>
    public const long DefaultFallbackPresents = 600;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly object _gate = new();
    private long _doneAt;

    public CaptureCompletion(int grace, long fallbackPresents)
    {
        Grace = grace;
        FallbackPresents = fallbackPresents;
    }

    /// <summary>Presents waited after every entry fired.</summary>
    public int Grace { get; }

    /// <summary>Presents after the last scheduled target before entries that never fired are given up.</summary>
    public long FallbackPresents { get; }

    /// <summary>The entries, in the order they were expected.</summary>
    public IReadOnlyList<string> Names
    {
        get
        {
            lock (_gate)
            {
                return [.. _order];
            }
        }
    }

    /// <summary>Expects <paramref name="name"/>, scheduled for <paramref name="present"/> (0: no present, e.g. a dispatch count).</summary>
    public void Expect(string name, long present)
    {
        lock (_gate)
        {
            if (_entries.TryAdd(name, new Entry(present)))
            {
                _order.Add(name);
                _doneAt = 0;
            }
        }
    }

    /// <summary>Sets the present <paramref name="name"/> is scheduled for, once it is known (a world or time trigger).</summary>
    public void Schedule(string name, long present)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(name, out var entry))
            {
                entry.Present = present;
            }
        }
    }

    /// <summary><paramref name="name"/> fired: its output is written.</summary>
    public void Fired(string name)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(name, out var entry))
            {
                entry.Fired = true;
            }
        }
    }

    /// <summary>The entries that have not fired, in order.</summary>
    public IReadOnlyList<string> Pending()
    {
        lock (_gate)
        {
            return [.. _order.Where(name => !_entries[name].Fired)];
        }
    }

    /// <summary>After present <paramref name="present"/>: the line to log and end the boot with, or null to go on.</summary>
    public string? AfterPresent(long present)
    {
        lock (_gate)
        {
            var pending = _order.Where(name => !_entries[name].Fired).ToList();

            if (pending.Count == 0)
            {
                if (_doneAt == 0)
                {
                    _doneAt = present;
                }

                if (present >= _doneAt + Grace)
                {
                    return $"[exit-when-done] all requested captures done at present {_doneAt}; ending";
                }

                return null;
            }

            long last = _entries.Values.Max(entry => entry.Present);

            if (last > 0 && present >= last + FallbackPresents)
            {
                return $"[exit-when-done] present {present} is {FallbackPresents} after the last scheduled target ({last}); ending; never fired: {string.Join(", ", pending)}";
            }

            return null;
        }
    }

    private sealed class Entry(long present)
    {
        public long Present { get; set; } = present;

        public bool Fired { get; set; }
    }
}
