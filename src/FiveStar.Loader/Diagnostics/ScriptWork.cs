// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// The input script's <c>capture</c> and <c>dump</c> lines a run has not written yet, and its <c>exit</c> line. A line is
/// expected while its time has not come, started once it asked for its present, and gone once its output is written.
/// After an exit line the run ends when no started line is left, or, when one never writes,
/// <see cref="FallbackPresents"/> presents after the exit line, naming it. Expected lines do not hold an exit.
/// </summary>
internal sealed class ScriptWork
{
    private readonly Dictionary<string, bool> _open = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly Lock _gate = new();
    private double _exitSeconds = double.NaN;
    private long _exitPresent = -1;
    private bool _exiting;

    public ScriptWork(long fallbackPresents)
    {
        FallbackPresents = fallbackPresents;
    }

    /// <summary>Presents after the exit line before started lines that never wrote are given up.</summary>
    public long FallbackPresents { get; }

    /// <summary>A line whose time has not come.</summary>
    public void Expect(string name)
    {
        lock (_gate)
        {
            if (_open.TryAdd(name, false))
            {
                _order.Add(name);
            }
        }
    }

    /// <summary>Forgets an expected line a reload dropped; false when it was not expected (unknown or started).</summary>
    public bool Withdraw(string name)
    {
        lock (_gate)
        {
            if (!_open.TryGetValue(name, out bool started) || started)
            {
                return false;
            }

            _open.Remove(name);
            _order.Remove(name);

            return true;
        }
    }

    /// <summary>The line asked for its present.</summary>
    public void Started(string name)
    {
        lock (_gate)
        {
            if (_open.TryAdd(name, true))
            {
                _order.Add(name);
            }

            _open[name] = true;
        }
    }

    /// <summary>The line's output is written; names that are not script lines are ignored.</summary>
    public void Fired(string name)
    {
        lock (_gate)
        {
            if (_open.Remove(name))
            {
                _order.Remove(name);
            }
        }
    }

    /// <summary>The lines not written yet, each with whether it started.</summary>
    public IReadOnlyList<(string Name, bool Started)> Open()
    {
        lock (_gate)
        {
            return [.. _order.Select(name => (name, _open[name]))];
        }
    }

    /// <summary>The exit line came, at <paramref name="seconds"/> of the script's clock.</summary>
    public void Exit(double seconds)
    {
        lock (_gate)
        {
            if (!_exiting)
            {
                _exitSeconds = seconds;
                Volatile.Write(ref _exiting, true);
            }
        }
    }

    /// <summary>At present <paramref name="present"/> (the last one seen): the line to log and end the run with, or null.</summary>
    public string? Due(long present)
    {
        if (!Volatile.Read(ref _exiting))
        {
            return null;
        }

        lock (_gate)
        {
            if (_exitPresent < 0)
            {
                _exitPresent = present;
            }

            var started = _order.Where(name => _open[name]).ToList();

            if (started.Count == 0)
            {
                return $"[input-script] exit line at {_exitSeconds:F1} s, every capture and dump started before it written; ending";
            }

            if (present >= _exitPresent + FallbackPresents)
            {
                return $"[input-script] exit line at {_exitSeconds:F1} s; present {present} is {FallbackPresents} after it; ending; never written: {string.Join(", ", started)}";
            }

            return null;
        }
    }
}
