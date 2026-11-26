// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Text;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// <c>--target-clear-census</c>: render targets and depths bound through a view of a texture another GPU-written texture
/// replaced at its address (<see cref="TargetClears.Replaced"/>). On the console both share the memory; here the draw
/// goes to the replaced texture while address-resolved reads find the new one, so what the draw wrote is never read
/// (the suspected cause of the blocky shadow pages: format-53 shadow textures re-made larger over the same address).
/// </summary>
internal static class ReplacedTargets
{
    /// <summary>Targets named per census line.</summary>
    private const int Named = 16;

    private static readonly ConcurrentDictionary<nint, string> Replaced = new();
    private static readonly ConcurrentDictionary<nint, (nint Resource, string Shape)> DepthViews = new();
    private static readonly ConcurrentDictionary<nint, long> Binds = new();

    /// <summary>The desktop texture <paramref name="desktop"/> was replaced at its address, as <paramref name="what"/> says.</summary>
    public static void Note(nint desktop, string what)
    {
        Replaced[desktop] = what;
    }

    /// <summary>A depth-stencil view of the desktop <paramref name="resource"/> was written at <paramref name="handle"/>.</summary>
    public static void NoteDepthView(nint handle, nint resource, string shape)
    {
        DepthViews[handle] = (resource, shape);
    }

    /// <summary>A render target of the desktop <paramref name="resource"/> was bound: counted when it was replaced.</summary>
    public static void Bound(nint resource)
    {
        if (Replaced.ContainsKey(resource))
        {
            Binds.AddOrUpdate(resource, 1, static (_, value) => value + 1);
        }
    }

    /// <summary>The depth-stencil view at <paramref name="handle"/> was bound: counted when its texture was replaced.</summary>
    public static void BoundDepth(nint handle)
    {
        if (DepthViews.TryGetValue(handle, out var view))
        {
            Bound(view.Resource);
        }
    }

    /// <summary>Census lines: each replaced texture bound since the boot began, with its binds.</summary>
    public static void Append(StringBuilder text)
    {
        var entries = Binds.ToArray();

        if (entries.Length == 0)
        {
            return;
        }

        text.Append($"\n[clears] {entries.Length} replaced textures bound as targets since the boot began, {entries.Sum(entry => entry.Value)} binds");

        foreach (var entry in entries.OrderByDescending(entry => entry.Value).Take(Named))
        {
            text.Append($"\n[clears]     bound after being replaced: 0x{entry.Key:x} {Replaced.GetValueOrDefault(entry.Key, "?")}, {entry.Value} binds");
        }
    }
}
