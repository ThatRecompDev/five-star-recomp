// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// <c>--log-skips &lt;name,...&gt;</c>: the stand-ins whose name holds one of these (case-insensitive) have their first
/// use while unbuilt and their skipped work logged one by one (the first <see cref="LoggedFirst"/>, then every
/// <see cref="LogEvery"/>-th), past the 64 names the skip warnings stop at: whether a one-shot pass, such as a bake into
/// an atlas, ran while its pipeline was still a stand-in. Off by default; debug only.
/// </summary>
internal static class SkipLog
{
    private const long LoggedFirst = 20;
    private const long LogEvery = 500;

    private static string[] _names = [];

    public static bool Enabled => _names.Length != 0;

    /// <summary>Takes a comma-separated list of name parts.</summary>
    public static void Configure(string list)
    {
        _names = list.Split(',').Select(name => name.Trim()).Where(name => name.Length > 0).ToArray();
    }

    /// <summary>Whether a pipeline named <paramref name="name"/> is logged.</summary>
    public static bool Matches(string name)
    {
        foreach (string part in _names)
        {
            if (name.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A list set the stand-in named <paramref name="name"/> for the first time, its pipeline not built.</summary>
    public static void FirstUse(string name)
    {
        if (Enabled && Matches(name))
        {
            ShimContext.Log?.Info($"  [skips] first use of {name} while its pipeline is not built (present {StandInUse.Frame})");
        }
    }

    /// <summary>The <paramref name="count"/>-th piece of work of the stand-in named <paramref name="name"/> was skipped.</summary>
    public static void Skipped(string name, long count)
    {
        if ((count <= LoggedFirst || count % LogEvery == 0) && Matches(name))
        {
            ShimContext.Log?.Info($"  [skips] work #{count} of {name} skipped: a stand-in (present {StandInUse.Frame})");
        }
    }
}
