// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// <c>--skip-pipelines &lt;text,...&gt;</c>: a pipeline whose programs' names hold one of the texts is treated as
/// unbuildable. It is neither loaded from the pipeline cache nor built, so it stays a stand-in and its draws are skipped;
/// each one is logged once. A way past a pipeline that hangs the GPU, to see what the rest of the frame does without it.
/// </summary>
internal static class SkippedPipelines
{
    private static readonly ConcurrentDictionary<string, byte> Logged = new(StringComparer.Ordinal);

    /// <summary>The texts; empty for none.</summary>
    public static IReadOnlyList<string> Texts { get; set; } = [];

    /// <summary>
    /// <c>--no-draws</c>: every pipeline is held, so no program is translated or compiled and every draw and dispatch of
    /// the game is skipped. The game runs as before (its code, streaming, memory, our shims) with a black picture: the
    /// frame time is then the game's own CPU and ours outside the draws, the floor no GPU work can lower. After Kelvaroq's
    /// 0061 (<c>VICTORY_NO_DRAWS</c>, GPL-3.0, facts only), itself after Cold Project VI, which measured 42-53 ms a frame so.
    /// </summary>
    public static bool All { get; set; }

    /// <summary>
    /// The end of a text that holds a mesh program only in its superstep form (<c>LinearDepthSkin@superstep</c>): such a
    /// text matches no pipeline name, so the pipeline is made as any other; its program is refused when it is made
    /// (<see cref="ProgramProducer.Prepare"/>) if the adapter would run it as the superstep form, the emulation of a 64-lane
    /// console wave on narrower hardware waves, and the pipeline stays a stand-in with its draws skipped.
    /// </summary>
    public const string SuperstepSuffix = "@superstep";

    /// <summary>The text that holds the superstep form of program <paramref name="name"/>; null for none.</summary>
    public static string? SuperstepText(string name)
    {
        return SuperstepText(name, Texts);
    }

    /// <summary>The one of <paramref name="texts"/> that holds the superstep form of program <paramref name="name"/>; null for none.</summary>
    public static string? SuperstepText(string name, IReadOnlyList<string> texts)
    {
        foreach (var text in texts)
        {
            if (text.EndsWith(SuperstepSuffix, StringComparison.Ordinal) && text.Length > SuperstepSuffix.Length
                && name.Contains(text[..^SuperstepSuffix.Length], StringComparison.Ordinal))
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>The texts of a comma-separated list, blanks dropped.</summary>
    public static IReadOnlyList<string> Parse(string list)
    {
        return list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>The first of <paramref name="texts"/> that <paramref name="names"/> holds; null for none.</summary>
    public static string? Match(string names, IReadOnlyList<string> texts)
    {
        return texts.FirstOrDefault(text => names.Contains(text, StringComparison.Ordinal));
    }

    /// <summary>True when the pipeline of <paramref name="names"/> is skipped; the first time, logged.</summary>
    public static bool Skips(string names)
    {
        return Skips(names, Texts);
    }

    /// <summary>True when one of <paramref name="texts"/> skips the pipeline of <paramref name="names"/>; the first time, logged.</summary>
    public static bool Skips(string names, IReadOnlyList<string> texts)
    {
        if (All || RayTracingPrograms.Skips(names))
        {
            return true;
        }

        var text = Match(names, texts);

        if (text is null)
        {
            return false;
        }

        if (Logged.TryAdd(names, 0))
        {
            ShimContext.Log?.Warn($"[d3d12_xs] pipeline {names} skipped (--skip-pipelines {text}): it stays a stand-in and its draws are skipped");
        }

        return true;
    }
}
