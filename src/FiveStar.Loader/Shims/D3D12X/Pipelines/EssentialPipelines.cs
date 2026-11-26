// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// Pipelines whose work is never skipped while a build is possible. Clears, blits and UI: full-screen clears and blits
/// (VS_Clear, PS_Clear*, VS_Blit*, PS_Blit*) and the Scaleform UI programs (sgd2 F* and V*); a skipped clear leaves its
/// target's last frame in place (boot w115: two credit cards overlaid). Their first use makes the programs and the
/// pipeline on the spot and waits up to <see cref="Limit"/>, outside every first-use budget (<see cref="StandInPipelines"/>).
/// Lighting (<see cref="Lighting"/>, on unless <c>--no-lighting-waits</c>): the cascade shadows, shadow masks, tiled
/// lighting, TL_PS, exposure, bloom and tone map wait up to <see cref="LightingLimit"/>. Which of them were stand-ins
/// decided the frame: w120 skipped the main cascade draw and washed out, w119 did not.
/// </summary>
internal static class EssentialPipelines
{
    /// <summary>How long a first use waits for a clear, blit or UI pipeline before its work is skipped as any other's.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>How long a first use waits for a lighting pipeline (as <c>--wait-pipelines</c>): mesh and TL_PS compiles take tens of seconds.</summary>
    public static readonly TimeSpan LightingLimit = PipelineWaits.DefaultLimit;

    private static readonly Regex Pattern = new(
        @"_t[0-9a-f]+_(?:VS|PS)_(?:Clear|Blit)|(?:^|\s)sgd2_[0-9a-f]{8}_t[0-9a-f]+_[FV][A-Z]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LightingPattern = new(
        @"CascadeShadows_draw|shadowmask~|deferred_tiled~|tiled_lighting~|TL_PS_|avgLuminanceCompute~|PS_CalculateExposure|PS_ResetExposure|Bloom|ToneMap",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Whether the lighting pipelines wait at first use (off with <c>--no-lighting-waits</c>).</summary>
    public static bool Lighting { get; set; } = true;

    /// <summary>Whether a pipeline named by its programs is a clear, blit or UI pipeline; never one --skip-pipelines holds.</summary>
    public static bool Matches(string name)
    {
        return Named(name, Pattern);
    }

    /// <summary>Whether a pipeline named by its programs belongs to the lighting chain (and <see cref="Lighting"/> is on).</summary>
    public static bool IsLighting(string name)
    {
        return Lighting && Named(name, LightingPattern);
    }

    private static bool Named(string name, Regex pattern)
    {
        if (name.Length == 0 || !pattern.IsMatch(name))
        {
            return false;
        }

        return !SkippedPipelines.Skips(name);
    }
}
