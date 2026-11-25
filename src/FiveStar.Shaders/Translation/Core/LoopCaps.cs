// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace FiveStar.Shaders.Translation.Core;

/// <summary>
/// Loop iterations one invocation may run, per stage (<see cref="LoopGuard"/>): compute, the geometry stages (vertex,
/// mesh, geometry) and pixel. <paramref name="StepsPerIteration"/>, when not 0, also bounds the block dispatch form
/// (which has no structured loops) at that many blocks per allowed iteration. Part of the translation options, so of
/// the program cache's identity.
/// </summary>
public sealed record LoopCaps(long Compute, long Geometry, long Pixel, long StepsPerIteration)
{
    /// <summary>Blocks per allowed iteration in the dispatch form of the <see cref="Victory"/> caps.</summary>
    private const long VictorySteps = 256;
    private const int Parts = 3;
    private const long DefaultCompute = 1 << 16;
    private const long DefaultGraphics = 1 << 12;
    private const long VictoryCompute = 4096;
    private const long VictoryGeometry = 256;
    private const long VictoryPixel = 128;

    /// <summary>The translator's own caps: 2^16 compute iterations, 2^12 elsewhere, 2^24 dispatch blocks.</summary>
    public static LoopCaps Default { get; } = new(DefaultCompute, DefaultGraphics, DefaultGraphics, 0);

    /// <summary>victory-gta6's caps (emit/lower.rs, count_iteration): compute 4,096, vertex and mesh 256, pixel 128.</summary>
    public static LoopCaps Victory { get; } = new(VictoryCompute, VictoryGeometry, VictoryPixel, VictorySteps);

    /// <summary>"victory", "default" or "compute,geometry,pixel"; null for anything else.</summary>
    public static LoopCaps? Parse(string text)
    {
        if (text.Equals("victory", StringComparison.OrdinalIgnoreCase))
        {
            return Victory;
        }

        if (text.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return Default;
        }

        var parts = text.Split(',');

        if (parts.Length != Parts || !parts.All(part => long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0))
        {
            return null;
        }

        var values = parts.Select(part => long.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        var (compute, geometry, pixel) = (values[0], values[1], values[^1]);

        return new LoopCaps(compute, geometry, pixel, VictorySteps);
    }

    /// <summary>The iterations of <paramref name="stage"/>.</summary>
    public long Iterations(ShaderStage stage)
    {
        return stage switch
        {
            ShaderStage.Compute => Compute,
            ShaderStage.Pixel => Pixel,
            _ => Geometry,
        };
    }

    /// <summary>The dispatch form's blocks for <paramref name="stage"/>.</summary>
    public long DispatchSteps(ShaderStage stage)
    {
        if (StepsPerIteration == 0)
        {
            return LoopGuard.DispatchSteps;
        }

        return Math.Min(LoopGuard.DispatchSteps, Iterations(stage) * StepsPerIteration);
    }
}
