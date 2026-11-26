// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Graphics.Replay;

/// <summary>What a replay gave: each output resource's bytes after the first run (in the capture's layout) and every run's GPU time.</summary>
public sealed record ReplayOutcome(IReadOnlyDictionary<string, byte[]> Outputs, IReadOnlyList<double> Milliseconds)
{
    private const int Halves = 2;

    /// <summary>The median run time.</summary>
    public double MedianMilliseconds
    {
        get
        {
            var sorted = Milliseconds.Order().ToList();
            var middle = sorted.Count / Halves;

            if (sorted.Count % Halves == 0)
            {
                return (sorted[middle - 1] + sorted[middle]) / Halves;
            }

            return sorted[middle];
        }
    }
}
