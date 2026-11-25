// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.Present;

namespace FiveStar.Loader.Host;

/// <summary>The values of <c>--frame-rate</c>: 30 (the console's, the default), 60 or uncapped.</summary>
internal static class FrameRates
{
    public static FrameRate Parse(string value)
    {
        switch (value.ToLowerInvariant())
        {
            case "30":
                return FrameRate.Console;
            case "60":
                return FrameRate.Sixty;
            case "uncapped" or "0":
                return FrameRate.Uncapped;
            default:
                throw new ArgumentException($"--frame-rate takes 30, 60 or uncapped, not '{value}'");
        }
    }
}
