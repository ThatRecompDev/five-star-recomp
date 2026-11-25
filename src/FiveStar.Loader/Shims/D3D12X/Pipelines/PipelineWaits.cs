// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// <c>--wait-pipelines &lt;name,...&gt;</c>: stand-ins whose name holds one of these (case-insensitive) are waited for when a
/// command list first sets them, up to <see cref="Limit"/>, instead of skipping their work (<see cref="StandInPipelines"/>).
/// For a frame that must be complete, such as the G-buffer of a cutscene's first world frames.
/// </summary>
internal sealed class PipelineWaits
{
    /// <summary>How long one stand-in is waited for before its work is skipped as without the option.</summary>
    public static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(60);

    private readonly string[] _names;

    public PipelineWaits(IEnumerable<string> names, TimeSpan limit)
    {
        _names = names.Select(name => name.Trim()).Where(name => name.Length > 0).ToArray();
        Limit = limit;
    }

    /// <summary>The waits of stand-in sets made from now on; null for none (the default).</summary>
    public static PipelineWaits? Current { get; set; }

    public TimeSpan Limit { get; }

    /// <summary>The waits for a comma-separated list of name parts; null when it names none.</summary>
    public static PipelineWaits? Parse(string? list)
    {
        var waits = new PipelineWaits((list ?? string.Empty).Split(','), DefaultLimit);

        if (waits._names.Length == 0)
        {
            return null;
        }

        return waits;
    }

    /// <summary>Whether a stand-in named <paramref name="name"/> is waited for; never one --skip-pipelines names, which stays a stand-in.</summary>
    public bool Matches(string name)
    {
        if (SkippedPipelines.Skips(name))
        {
            return false;
        }

        foreach (string part in _names)
        {
            if (name.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
