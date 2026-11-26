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

            int skipped = Math.Max(0, first - dwords[MostDetailedMipDword]);
            dwords[MostDetailedMipDword] = Math.Max(0, dwords[MostDetailedMipDword] - first);

            if (dwords[MipLevelsDword] != AllLevels)
            {
                dwords[MipLevelsDword] = Math.Max(1, Math.Min(dwords[MipLevelsDword] - skipped, resource.MipLevels - dwords[MostDetailedMipDword]));
            }

            *(float*)(dwords + MinLodClampDword) = Math.Max(0, *(float*)(dwords + MinLodClampDword) - first);

            return true;
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
