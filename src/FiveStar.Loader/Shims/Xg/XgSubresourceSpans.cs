// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.Xg;

/// <summary>
/// The bytes of a resource laid out by <see cref="XgLayoutBuilder"/> that one subresource (mip + slice * mips) of its
/// first plane covers: every element <see cref="XgAddress.Element"/> addresses for it lies in the span, so a copy of
/// the subresource (<see cref="XgCopier"/>) reads nothing outside it. A tail mip spans its slice's tail block; a thick
/// (3D) layout or one of several planes spans the whole resource.
/// </summary>
internal static class XgSubresourceSpans
{
    private const uint Texture3D = 4;

    /// <summary>The start and end (exclusive) of subresource <paramref name="subresource"/>, clamped to the resource.</summary>
    public static (ulong Start, ulong End) Of(in XgSurface surface, int subresource)
    {
        if (surface.PlaneCount != 1 || surface.Desc.Dimension == Texture3D)
        {
            return (0, surface.Size);
        }

        ref readonly XgPlane plane = ref surface.Planes[0];

        if (plane.Thick || plane.MipCount <= 0)
        {
            return (0, surface.Size);
        }

        int mipIndex = subresource % plane.MipCount;
        int slice = subresource / plane.MipCount;
        ref readonly XgMip mip = ref plane.Mips[mipIndex];
        ulong sliceStart = plane.Offset + ((ulong)slice * plane.SliceStride);
        ulong elementBytes = (ulong)plane.BytesPerElement;

        ulong start;
        ulong end;

        if (plane.BlockBytes == 0)
        {
            start = sliceStart + mip.Offset;
            end = start + ((((ulong)Math.Max(mip.HeightElements, 1) - 1) * mip.PitchElements) + mip.WidthElements) * elementBytes;
        }
        else if (mip.InTail != 0)
        {
            start = sliceStart;
            end = sliceStart + (ulong)plane.BlockBytes;
        }
        else
        {
            var swizzle = plane.Swizzle;
            ulong rows = (mip.HeightElements + (ulong)swizzle.TileHeight - 1) / (ulong)swizzle.TileHeight;
            ulong columns = mip.PitchElements / (ulong)swizzle.TileWidth;
            start = sliceStart + mip.Offset;
            end = start + (rows * columns * (ulong)plane.BlockBytes);
        }

        return (Math.Min(start, surface.Size), Math.Min(end, surface.Size));
    }
}
