// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using FiveStar.Loader.Shims.D3D12X.Resources;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// With <c>--trace-views</c>: the slot-70 texture views counted by their levels (the texture's mips, the view's
/// MostDetailedMip + MipLevels, its LOD clamp and the desc's dword at +0x3C), by what was registered at their address
/// and by whether they got a texture. For w180: which views the game makes of a streamed texture whose large levels
/// are not in memory, and which of them go null. CPU only; logged at each power of two of the views made.
/// </summary>
internal static unsafe class ViewCensus
{
    private const int MostDetailedMipDword = 4;
    private const int MipLevelsDword = 5;
    private const int MinLodClampDword = 7;
    private const int Lines = 48;
    private const long FirstReport = 1 << 16;

    private static readonly ConcurrentDictionary<string, long> Counts = new();

    public static bool Enabled => ViewTrace.Enabled;

    /// <summary>A texture view at an address was written: <paramref name="before"/> was registered there, <paramref name="texture"/> is what it reads.</summary>
    public static void Note(ResourceDescription* resource, AddressViewDescription* view, GpuTexture? before, GpuTexture? texture)
    {
        if (resource == null || view->Address == 0 || resource->Dimension == ResourceDescription.BufferDimension || resource->Format == 0)
        {
            return;
        }

        int* dwords = (int*)view;
        float clamp = *(float*)(dwords + MinLodClampDword);
        string outcome = "a new texture";

        if (texture is null)
        {
            outcome = "null view";
        }
        else if (texture == before)
        {
            outcome = "the texture registered";
        }

        string key = string.Create(CultureInfo.InvariantCulture, $"{resource->MipLevels} mips, view dimension {view->ViewDimension} mips {dwords[MostDetailedMipDword]}+{dwords[MipLevelsDword]} clamp {clamp:0.##} +3C {view->Unknown3C}; {Relation(resource, view->Address, before)}; reads {outcome}");
        Counts.AddOrUpdate(key, 1, static (_, count) => count + 1);
    }

    /// <summary>The census, largest counts first, when <paramref name="views"/> (all slot-70 views so far) is a power of two worth a report; else null.</summary>
    public static string? Report(long views)
    {
        if (!Enabled || views < FirstReport || !System.Numerics.BitOperations.IsPow2(views))
        {
            return null;
        }

        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"  [view census] {Counts.Count} kinds of texture view at an address:"));

        foreach (var (key, count) in Counts.OrderByDescending(entry => entry.Value).Take(Lines))
        {
            text.Append(CultureInfo.InvariantCulture, $"\n      {count} x {key}");
        }

        return text.ToString();
    }

    private static string Relation(ResourceDescription* resource, ulong address, GpuTexture? before)
    {
        if (before is null)
        {
            return "nothing registered there";
        }

        string kind = before.GpuWritten ? "GPU-written" : "read-only";

        if (before.Address != address)
        {
            return $"inside a {kind} texture that starts before";
        }

        int dropped = ResidentLevels.Dropped(*resource, before.Console);

        if (dropped >= 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"its texture without its {dropped} largest levels registered");
        }

        if (ResidentLevels.Dropped(before.Console, *resource) > 0)
        {
            return "a larger texture of its levels registered";
        }

        return $"another {kind} texture registered at its address";
    }
}
