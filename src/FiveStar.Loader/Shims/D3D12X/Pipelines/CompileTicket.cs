// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// One compile <see cref="CompileBudget"/> admitted, which <see cref="CompileBudget.Exit"/> takes back: its estimate,
/// whether it is large (counted in the budget's list, <see cref="CompileBudget.TinyShaderBytes"/> or more) and heavy,
/// when it started and the RAM-backed bytes then. Exit removes exactly this entry: matching by estimate removed another
/// compile's of the same estimate, or none when the bytes Exit was given differed from Enter's, and the entry left behind
/// kept a quarter of a multi-gigabyte estimate reserved for good (issue #13: setup stalled at 2044 of 3880).
/// </summary>
internal sealed class CompileTicket
{
    public CompileTicket(long estimate, bool large, bool heavy, long started, long startBytes)
    {
        Estimate = estimate;
        Large = large;
        Heavy = heavy;
        Started = started;
        StartBytes = startBytes;
    }

    public long Estimate { get; }

    public bool Large { get; }

    public bool Heavy { get; }

    public long Started { get; }

    public long StartBytes { get; }

    /// <summary>Set by the first <see cref="CompileBudget.Exit"/>: a second one changes nothing.</summary>
    public bool Ended { get; set; }
}
