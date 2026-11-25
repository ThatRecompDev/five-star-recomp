// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>A pipeline host's compile: how it ended, the HRESULT it answered (0 without one), its time and peak memory.</summary>
internal sealed record PipelineHostResult(PipelineHostOutcome Outcome, int HResult, double Seconds, long PeakBytes)
{
    /// <summary>Whether the pipeline is too heavy for this machine: the host passed a limit.</summary>
    public bool Killed => Outcome is PipelineHostOutcome.KilledMemory or PipelineHostOutcome.KilledTime;
}
