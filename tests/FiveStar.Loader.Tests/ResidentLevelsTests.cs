// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X;
using FiveStar.Loader.Shims.D3D12X.Resources;
using FiveStar.Loader.Shims.Xg;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>A slot-70 view that skips a streamed texture's large levels reads a texture of the levels it reads.</summary>
public sealed unsafe class ResidentLevelsTests
{
    private const int Texture2D = 3;
    private const int SrvTexture2D = 4;
    private const int SrvTexture2DArray = 5;
    private const int Bc1 = 71;
    private const int Bc7 = 98;
    private const int MostDetailedMip = 4;
    private const int MipLevels = 5;
    private const int MinLodClamp = 7;

    /// <summary>w179's packed 1024x1024 BC1 textures stood 0x4000 bytes apart: levels 3 to 8 take 0x3000 of the 0xab000.</summary>
    [Fact]
    public void A_view_from_level_3_reads_a_texture_of_the_small_levels_at_the_same_bytes()
    {
        var resource = Resource(1024, 1024, 9, Bc1);
        var view = View(SrvTexture2D, first: 3, levels: 6, clamp: 0);

        Assert.Equal(0xab000ul, Bytes(resource));
        Assert.True(ResidentLevels.Reduce(ref resource, ref view));

        Assert.Equal((128ul, 128u, (ushort)6), (resource.Width, resource.Height, resource.MipLevels));
        Assert.Equal(0x3000ul, Bytes(resource));
        Assert.Equal((0, 6), (Word(view, MostDetailedMip), Word(view, MipLevels)));
    }

    [Fact]
    public void A_lod_clamp_skips_levels_as_the_most_detailed_mip_does()
    {
        var resource = Resource(512, 512, 8, Bc7);
        var view = View(SrvTexture2D, first: 1, levels: 7, clamp: 3);

        Assert.True(ResidentLevels.Reduce(ref resource, ref view));

        Assert.Equal((64ul, 64u, (ushort)5), (resource.Width, resource.Height, resource.MipLevels));
        Assert.Equal((0, 5, 0f), (Word(view, MostDetailedMip), Word(view, MipLevels), BitConverter.Int32BitsToSingle(Word(view, MinLodClamp))));
    }

    [Fact]
    public void Levels_that_would_move_stay_in_the_texture()
    {
        // From level 7 the two last levels would leave their tail slots: the view keeps the levels up to where none moves.
        var resource = Resource(1024, 1024, 9, Bc1);
        var whole = resource;
        var view = View(SrvTexture2D, first: 7, levels: -1, clamp: 0);

        Assert.False(ResidentLevels.SameBytes(whole, 7));
        Assert.True(ResidentLevels.Reduce(ref resource, ref view));

        int dropped = whole.MipLevels - resource.MipLevels;
        Assert.InRange(dropped, 1, 6);
        Assert.True(ResidentLevels.SameBytes(whole, dropped));
        Assert.Equal((7 - dropped, -1), (Word(view, MostDetailedMip), Word(view, MipLevels)));
    }

    [Fact]
    public void A_view_from_level_0_or_of_an_array_keeps_the_whole_texture()
    {
        var resource = Resource(1024, 1024, 9, Bc1);
        var whole = View(SrvTexture2D, first: 0, levels: -1, clamp: 0);
        var array = View(SrvTexture2DArray, first: 3, levels: 6, clamp: 0);

        Assert.False(ResidentLevels.Reduce(ref resource, ref whole));
        Assert.False(ResidentLevels.Reduce(ref resource, ref array));
        Assert.Equal((1024ul, (ushort)9, 3), (resource.Width, resource.MipLevels, Word(array, MostDetailedMip)));
    }

    private static ResourceDescription Resource(uint width, uint height, ushort mips, int format)
    {
        return new ResourceDescription
        {
            Dimension = Texture2D, Width = width, Height = height, DepthOrArraySize = 1, MipLevels = mips,
            Format = format, SampleCount = 1, Layout = 0x105,
        };
    }

    private static AddressViewDescription View(int dimension, int first, int levels, float clamp)
    {
        var view = new AddressViewDescription { Format = Bc1, ViewDimension = dimension, Address = 0x279aaa60000 };
        int* words = (int*)&view;
        words[MostDetailedMip] = first;
        words[MipLevels] = levels;
        words[MinLodClamp] = BitConverter.SingleToInt32Bits(clamp);

        return view;
    }

    private static int Word(AddressViewDescription view, int index)
    {
        return ((int*)&view)[index];
    }

    private static ulong Bytes(ResourceDescription resource)
    {
        XgLayoutBuilder.Build(GpuTextures.ToXg(&resource), out var surface);

        return surface.Size;
    }
}
