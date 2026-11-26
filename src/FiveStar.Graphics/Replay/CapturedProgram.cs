// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Graphics.Replay;

/// <summary>
/// The captured program: its translated name, profile prefix and the files kept beside the manifest (paths relative to
/// the capture folder, null when the loader had none): the DXIL the pipeline was built from, the HLSL it was compiled
/// from, the console program (FSSM) it was translated from, the defines the loader put before it and the runtime headers.
/// </summary>
public sealed record CapturedProgram(string Name, string Profile, string? Dxil, string? Hlsl, string? Fssm, string? Header)
{
    /// <summary>The translation options the loader ran with, as text; null when not known.</summary>
    public string? Translation { get; init; }

    /// <summary>The folder (relative) holding the runtime headers the HLSL includes; null when not kept.</summary>
    public string? Includes { get; init; }
}
