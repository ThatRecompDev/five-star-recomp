// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Text;
using FiveStar.Loader.Diagnostics;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// Part of <c>--d3d12-trace-draws</c>: every draw, dispatch and ExecuteIndirect between two captured presents, counted
/// by pipeline and first render target, recorded or skipped (a stand-in's work), so a capture shows which passes ran
/// into which targets and which the frame lost.
/// </summary>
internal sealed class DrawCensus
{
    private const int LinesShown = 60;

    private readonly ConcurrentDictionary<(nint Pipeline, nint Target, bool Skipped), long> _counts = new();
    private readonly LoaderLog _log;
    private long _since;

    public DrawCensus(LoaderLog log)
    {
        _log = log;
    }

    /// <summary>Counts one piece of work.</summary>
    public void Add(nint pipeline, nint target, bool skipped)
    {
        _counts.AddOrUpdate((pipeline, target, skipped), 1, static (_, count) => count + 1);
    }

    /// <summary>Logs and clears the counts, the largest first; <paramref name="name"/> names a pipeline, <paramref name="describe"/> a target.</summary>
    public void Report(long present, Func<nint, bool, string> name, Func<nint, string> describe)
    {
        var counts = _counts.ToArray();
        _counts.Clear();

        var recorded = counts.Where(entry => !entry.Key.Skipped).Sum(entry => entry.Value);
        var skipped = counts.Where(entry => entry.Key.Skipped).Sum(entry => entry.Value);
        var text = new StringBuilder($"  [census] presents {_since}-{present}: {recorded} recorded, {skipped} skipped, {counts.Length} pipeline-target pairs");
        _since = present;

        foreach (var (key, count) in counts.OrderByDescending(entry => entry.Value).Take(LinesShown))
        {
            string target = "no target";

            if (key.Target != 0)
            {
                target = $"0x{key.Target:x} {describe(key.Target)}";
            }

            string kind = "recorded";

            if (key.Skipped)
            {
                kind = "skipped";
            }

            text.Append($"\n    {count} {kind} {name(key.Pipeline, key.Skipped)} -> {target}");
        }

        _log.Info(text.ToString());
    }
}
