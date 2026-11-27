// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.Xg;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// The levels a slot-70 Texture2D view reads. The game keeps only a streamed texture's small levels, which come first in
/// memory, and packs the next texture where the large ones would be; its view names the whole texture and skips those
/// with MostDetailedMip or ResourceMinLODClamp (an integer, set at 0x14501f530). In w179 1024x1024 textures of 0xab000
/// bytes stood 0x4000 apart: each one made from its whole desc retired its neighbours, whose views went null (65,672
/// released textures with views held), and the G-buffer's albedo read 0. Such a view reads a texture of its levels
/// only, whose bytes are the same ones at the same address.
/// </summary>
internal static unsafe class ResidentLevels
{
    /// <summary>D3D12_RESOURCE_DIMENSION_TEXTURE2D and D3D12_SRV_DIMENSION_TEXTURE2D.</summary>
    private const int ResourceTexture2D = 3;
    private const int ViewTexture2D = 4;

    /// <summary>Dwords of a Texture2D SRV desc: MostDetailedMip, MipLevels (-1: all), PlaneSlice, ResourceMinLODClamp (a float).</summary>
    private const int MostDetailedMipDword = 4;
    private const int MipLevelsDword = 5;
    private const int MinLodClampDword = 7;

    private const int AllLevels = -1;

    /// <summary>
    /// Drops the levels <paramref name="view"/> never reads from <paramref name="resource"/> and renumbers the view's;
    /// false, with both as they were, when it reads from level 0, is not a plain 2D view, or the smaller texture's
    /// levels would not lie where the whole one's do.
    /// </summary>
    public static bool Reduce(ref ResourceDescription resource, ref AddressViewDescription view)
    {
        fixed (AddressViewDescription* fields = &view)
        {
            int* dwords = (int*)fields;
            int first = First(resource, dwords);

            while (first > 0 && !SameBytes(resource, first))
            {
                first--;
            }

            if (first == 0)
            {
                return false;
            }

            resource.Width = Math.Max(1UL, resource.Width >> first);
            resource.Height = Math.Max(1u, resource.Height >> first);
            resource.MipLevels -= (ushort)first;
            Renumber(dwords, first, resource.MipLevels);

            return true;
        }
    }

    /// <summary>
    /// Renumbers <paramref name="view"/>, of <paramref name="resource"/>, for <paramref name="registered"/> when that
    /// is the texture of the levels from one the view does not read before (another view of the same texture made it);
    /// false, with the view as it was, when it is not.
    /// </summary>
    public static bool Onto(in ResourceDescription resource, in ResourceDescription registered, ref AddressViewDescription view)
    {
        fixed (AddressViewDescription* fields = &view)
        {
            int dropped = Dropped(resource, registered);

            if (dropped <= 0 || dropped > First(resource, (int*)fields))
            {
                return false;
            }

            Renumber((int*)fields, dropped, registered.MipLevels);

            return true;
        }
    }

    /// <summary>
    /// The levels <paramref name="whole"/> has before those of <paramref name="part"/>, the texture of its smaller
    /// levels at the same bytes (0: the same texture); -1 when it is not that.
    /// </summary>
    public static int Dropped(in ResourceDescription whole, in ResourceDescription part)
    {
        int dropped = whole.MipLevels - part.MipLevels;

        if (dropped < 0 || whole.Format != part.Format || whole.Dimension != part.Dimension || whole.DepthOrArraySize != part.DepthOrArraySize
            || whole.Layout != part.Layout || Math.Max(1UL, whole.Width >> dropped) != part.Width || Math.Max(1u, whole.Height >> dropped) != part.Height)
        {
            return -1;
        }

        if (dropped == 0)
        {
            return 0;
        }

        bool plain = whole.Dimension == ResourceTexture2D && whole.DepthOrArraySize <= 1 && whole.SampleCount <= 1;

        if (plain && SameBytes(whole, dropped))
        {
            return dropped;
        }

        return -1;
    }

    /// <summary>
    /// Drops the largest levels of <paramref name="resource"/>, as few as leave a texture of at most <paramref name="bytes"/>
    /// with its levels at the same bytes; false, with the desc as it was, when none does.
    /// </summary>
    public static bool Fit(ref ResourceDescription resource, ulong bytes)
    {
        bool plain = resource.Dimension == ResourceTexture2D && resource.DepthOrArraySize <= 1 && resource.SampleCount <= 1;

        for (int dropped = 1; plain && dropped < resource.MipLevels; dropped++)
        {
            var smaller = resource;
            smaller.Width = Math.Max(1UL, resource.Width >> dropped);
            smaller.Height = Math.Max(1u, resource.Height >> dropped);
            smaller.MipLevels -= (ushort)dropped;
            XgLayoutBuilder.Build(GpuTextures.ToXg(&smaller), out var part);

            if (part.Size <= bytes && SameBytes(resource, dropped))
            {
                resource = smaller;

                return true;
            }
        }

        return false;
    }

    /// <summary>A Texture2D SRV desc's levels counted from <paramref name="dropped"/> levels later, in a texture of <paramref name="levels"/>.</summary>
    internal static void Renumber(int* view, int dropped, int levels)
    {
        int skipped = Math.Max(0, dropped - view[MostDetailedMipDword]);
        view[MostDetailedMipDword] = Math.Max(0, view[MostDetailedMipDword] - dropped);

        if (view[MipLevelsDword] != AllLevels)
        {
            view[MipLevelsDword] = Math.Max(1, Math.Min(view[MipLevelsDword] - skipped, levels - view[MostDetailedMipDword]));
        }

        *(float*)(view + MinLodClampDword) = Math.Max(0, *(float*)(view + MinLodClampDword) - dropped);
    }

    /// <summary>Whether <paramref name="view"/>, an SRV desc, is a Texture2D one: its levels can be counted from another.</summary>
    internal static bool Renumbers(int* view)
    {
        return view[1] == ViewTexture2D;
    }

    /// <summary>A Texture2D SRV desc's levels counted in a texture with <paramref name="added"/> larger levels before them.</summary>
    internal static void Later(int* view, int added)
    {
        view[MostDetailedMipDword] += added;

        if (*(float*)(view + MinLodClampDword) > 0)
        {
            *(float*)(view + MinLodClampDword) += added;
        }
    }

    /// <summary>The first level the view reads: the larger of MostDetailedMip and the whole part of its LOD clamp; 0 for no plain 2D view.</summary>
    private static int First(in ResourceDescription resource, int* view)
    {
        if (view[1] != ViewTexture2D || resource.Dimension != ResourceTexture2D || resource.DepthOrArraySize > 1 || resource.SampleCount > 1 || resource.MipLevels <= 1)
        {
            return 0;
        }

        float clamp = *(float*)(view + MinLodClampDword);
        int first = view[MostDetailedMipDword];

        if (clamp >= 1 && clamp < resource.MipLevels)
        {
            first = Math.Max(first, (int)clamp);
        }

        return Math.Clamp(first, 0, resource.MipLevels - 1);
    }

    /// <summary>Whether the texture of <paramref name="resource"/>'s levels from <paramref name="first"/> has each of them at the whole texture's offset, in its layout.</summary>
    internal static bool SameBytes(in ResourceDescription resource, int first)
    {
        var smaller = resource;
        smaller.Width = Math.Max(1UL, resource.Width >> first);
        smaller.Height = Math.Max(1u, resource.Height >> first);
        smaller.MipLevels -= (ushort)first;

        XgLayoutBuilder.Build(GpuTextures.ToXg(&smaller), out var part);

        fixed (ResourceDescription* whole = &resource)
        {
            XgLayoutBuilder.Build(GpuTextures.ToXg(whole), out var all);

            return Same(all, part, first);
        }
    }

    private static bool Same(in XgSurface all, in XgSurface part, int first)
    {
        ref readonly XgPlane whole = ref all.Planes[0];
        ref readonly XgPlane plane = ref part.Planes[0];

        if (all.PlaneCount != 1 || part.PlaneCount != 1 || whole.BlockBytes != plane.BlockBytes || whole.MipCount != plane.MipCount + first)
        {
            return false;
        }

        for (int level = 0; level < plane.MipCount; level++)
        {
            ref readonly XgMip from = ref whole.Mips[level + first];
            ref readonly XgMip to = ref plane.Mips[level];

            if (from.Offset != to.Offset || from.InTail != to.InTail || from.TailX != to.TailX || from.TailY != to.TailY || from.PitchElements != to.PitchElements)
            {
                return false;
            }
        }

        return true;
    }
}
