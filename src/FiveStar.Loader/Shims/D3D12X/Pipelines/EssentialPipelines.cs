// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// Pipelines whose work is never skipped while a build is possible: full-screen clears and blits (VS_Clear, PS_Clear*,
/// VS_Blit*, PS_Blit*) and the Scaleform UI programs (sgd2 F* and V*). A skipped clear leaves its target's last frame
/// in place: in boot w115 the UI target's VS_Clear + PS_Clear stand-in kept two credit cards overlaid. Their first use
/// makes the programs and the pipeline on the spot and waits up to <see cref="Limit"/>, outside every first-use budget
/// (<see cref="StandInPipelines"/>); the programs are a few kilobytes, which the compile budget never holds up.
/// </summary>
internal static class EssentialPipelines
{
    /// <summary>How long a first use waits for an essential pipeline before its work is skipped as any other's.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private static readonly Regex Pattern = new(
        @"_t[0-9a-f]+_(?:VS|PS)_(?:Clear|Blit)|(?:^|\s)sgd2_[0-9a-f]{8}_t[0-9a-f]+_[FV][A-Z]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Whether a pipeline named by its programs is essential; never one --skip-pipelines holds.</summary>
    public static bool Matches(string name)
    {
        if (name.Length == 0 || !Pattern.IsMatch(name))
        {
            return false;
        }

        return !SkippedPipelines.Skips(name);
    }
}
