// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

// Ported from victory-fork (github.com/fxceless/victory-fork, commit 578d459, crates/victory-gpu/src/texture.rs `alias`),
// itself built on VIctory. Copyright (c) the VIctory and victory-fork contributors; GPL-3.0-or-later.

using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.States;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// Records the copy of an R32_UINT source's texels into its R9G9B9E5 shadow (<see cref="SharedExponentAlias"/>): every
/// level of every layer into the buffer, then from the buffer into the shadow (the desktop copies between two formats
/// of one texel width in no other way). The source returns to the state it was in; the shadow ends in a read state.
/// </summary>
internal static unsafe class SharedExponentCopy
{
    /// <summary>D3D12_RESOURCE_STATE_COMMON, COPY_DEST, COPY_SOURCE, NON_PIXEL_SHADER_RESOURCE, PIXEL_SHADER_RESOURCE.</summary>
    public const int Common = 0;
    public const int CopyDest = 0x400;
    public const int CopySource = 0x800;
    public const int NonPixelRead = 0x40;
    public const int PixelRead = 0x80;

    /// <summary>ID3D12GraphicsCommandList::CopyTextureRegion (d3d12.h).</summary>
    private const int CopyTextureRegionSlot = 16;

    /// <summary>D3D12_TEXTURE_COPY_LOCATION: 48 bytes; the type at +8, the subresource or footprint from +16.</summary>
    private const int LocationBytes = 48;
    private const int TypeField = 8;
    private const int UnionField = 16;
    private const int FootprintField = 24;

    /// <summary>D3D12_SUBRESOURCE_FOOTPRINT after its format: width, height, depth, row pitch.</summary>
    private const int WidthField = 4;
    private const int HeightField = 8;
    private const int DepthField = 12;
    private const int PitchField = 16;
    private const int SubresourceType = 0;
    private const int FootprintType = 1;

    /// <summary>
    /// The state the shadow is read in on a list whose legal states are <paramref name="legal"/>: both shader-resource
    /// states on a direct list, the non-pixel one on a compute list.
    /// </summary>
    public static int ReadState(int legal)
    {
        if ((legal & PixelRead) != 0)
        {
            return PixelRead | NonPixelRead;
        }

        return NonPixelRead;
    }

    /// <summary>
    /// Records the copy on <paramref name="list"/> with the source in <paramref name="sourceState"/> and the shadow in
    /// <paramref name="shadowState"/>; returns the shadow's state after it (<see cref="ReadState"/>), or -1 when a state
    /// involved is one the list's type cannot name (nothing recorded).
    /// </summary>
    public static int Record(nint list, SharedExponentAlias alias, int sourceState, int shadowState, int legal)
    {
        int read = ReadState(legal);

        if (((sourceState | shadowState | CopyDest | CopySource | read) & ~legal) != 0)
        {
            return -1;
        }

        nint source = alias.Source.Desktop;
        Move(list, source, sourceState, CopySource);
        Move(list, alias.Shadow, shadowState, CopyDest);
        BarrierWriter.Transition(list, alias.Buffer, Common, CopyDest);

        for (int part = 0; part < alias.Written.Length; part++)
        {
            Copy(list, Placed(alias.Buffer, alias.Written[part]), Part(source, part));
        }

        BarrierWriter.Transition(list, alias.Buffer, CopyDest, CopySource);

        for (int part = 0; part < alias.Read.Length; part++)
        {
            Copy(list, Part(alias.Shadow, part), Placed(alias.Buffer, alias.Read[part]));
        }

        BarrierWriter.Transition(list, alias.Buffer, CopySource, Common);
        Move(list, alias.Shadow, CopyDest, read);
        Move(list, source, CopySource, sourceState);

        return read;
    }

    private static void Move(nint list, nint resource, int before, int after)
    {
        if (before != after)
        {
            BarrierWriter.Transition(list, resource, before, after);
        }
    }

    private static void Copy(nint list, Location destination, Location source)
    {
        ((delegate* unmanaged<nint, byte*, uint, uint, uint, byte*, void*, void>)ComObject.Slot(list, CopyTextureRegionSlot))(list, destination.Bytes, 0, 0, 0, source.Bytes, null);
    }

    private static Location Part(nint texture, int subresource)
    {
        var location = default(Location);
        *(nint*)location.Bytes = texture;
        *(int*)(location.Bytes + TypeField) = SubresourceType;
        *(uint*)(location.Bytes + UnionField) = (uint)subresource;

        return location;
    }

    private static Location Placed(nint buffer, CopyFootprint footprint)
    {
        var location = default(Location);
        *(nint*)location.Bytes = buffer;
        *(int*)(location.Bytes + TypeField) = FootprintType;
        *(ulong*)(location.Bytes + UnionField) = footprint.Offset;

        byte* fields = location.Bytes + FootprintField;
        *(int*)fields = footprint.Format;
        *(uint*)(fields + WidthField) = footprint.Width;
        *(uint*)(fields + HeightField) = footprint.Height;
        *(uint*)(fields + DepthField) = footprint.Depth;
        *(uint*)(fields + PitchField) = footprint.RowPitch;

        return location;
    }

    /// <summary>A D3D12_TEXTURE_COPY_LOCATION on the stack.</summary>
    private struct Location
    {
        public fixed byte Bytes[LocationBytes];
    }
}
