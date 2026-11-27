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

    /// <summary>
    /// The game makes three views of a texture (w180: 1024x1024 with 9 mips, then from levels 3 and 4): each one's
    /// texture retired the last one's at the same address and its views went null.
    /// </summary>
    [Fact]
    public void Views_of_one_texture_from_several_levels_read_one_texture()
    {
        var whole = Resource(1024, 1024, 9, Bc1);
        var fromThree = whole;
        var third = View(SrvTexture2D, first: 3, levels: 6, clamp: 0);
        Assert.True(ResidentLevels.Reduce(ref fromThree, ref third));

        var fourth = View(SrvTexture2D, first: 4, levels: 5, clamp: 0);
        var main = View(SrvTexture2D, first: 0, levels: 9, clamp: 0);

        Assert.True(ResidentLevels.Onto(whole, fromThree, ref fourth));
        Assert.Equal((1, 5), (Word(fourth, MostDetailedMip), Word(fourth, MipLevels)));
        Assert.False(ResidentLevels.Onto(whole, fromThree, ref main));
        Assert.Equal((3, -1), (ResidentLevels.Dropped(whole, fromThree), ResidentLevels.Dropped(fromThree, whole)));
    }

    [Fact]
    public void A_released_texture_s_views_read_the_larger_texture_of_its_levels_from_its_first()
    {
        var whole = Resource(1024, 1024, 9, Bc1);
        var small = Resource(128, 128, 6, Bc1);
        var released = new GpuTexture(0x279aaa60000, small, 1, Bc1, 0, false, new XgSurface { Size = 0x3000 });
        var larger = new GpuTexture(0x279aaa60000, whole, 2, Bc1, 0, false, new XgSurface { Size = 0xab000 });
        var other = new GpuTexture(0x279aaa60000, Resource(256, 512, 8, Bc1), 3, Bc1, 0, false, new XgSurface { Size = 0x2b000 });
        var view = View(SrvTexture2D, first: 1, levels: 5, clamp: 0);

        Assert.Equal((3, 0, -3), (ReleasedTextureViews.Shift(released, larger), ReleasedTextureViews.Shift(released, other), ReleasedTextureViews.Shift(larger, released)));

        ResidentLevels.Later((int*)&view, 3);
        Assert.Equal((4, 5), (Word(view, MostDetailedMip), Word(view, MipLevels)));
    }

    /// <summary>The next texture 0x4000 bytes into a 1024x1024 one leaves it its levels from 128x128; one in its first bytes leaves none.</summary>
    [Fact]
    public void A_texture_another_is_placed_inside_keeps_the_levels_before_it()
    {
        var resource = Resource(1024, 1024, 9, Bc1);
        var none = resource;

        Assert.True(ResidentLevels.Fit(ref resource, 0x4000));
        Assert.Equal((128ul, 128u, (ushort)6, 0x3000ul), (resource.Width, resource.Height, resource.MipLevels, Bytes(resource)));
        Assert.False(ResidentLevels.Fit(ref none, 0x800));
        Assert.Equal((ushort)9, none.MipLevels);

        var view = View(SrvTexture2D, first: 0, levels: 9, clamp: 0);
        ResidentLevels.Renumber((int*)&view, 3, resource.MipLevels);
        Assert.Equal((0, 6), (Word(view, MostDetailedMip), Word(view, MipLevels)));
    }

    /// <summary>w186: a 1024x1024 texture's 0xab000 bytes took in the 64x64 ones 0x4000 and 0xc000 behind it, and their views went null.</summary>
    [Fact]
    public void A_texture_whose_views_are_held_is_not_retired_by_one_that_starts_elsewhere()
    {
        var held = new GpuTexture(0x289da878000, Resource(64, 64, 5, Bc1), 1, Bc1, 0, false, new XgSurface { Size = 0x1000 });
        var free = new GpuTexture(0x289da880000, Resource(64, 64, 5, Bc1), 2, Bc1, 0, false, new XgSurface { Size = 0x1000 });
        var large = new GpuTexture(0x289da874000, Resource(1024, 1024, 9, Bc1), 3, Bc1, 0, false, new XgSurface { Size = 0xab000 });
        var again = new GpuTexture(0x289da878000, Resource(128, 128, 6, Bc1), 4, Bc1, 0, false, new XgSurface { Size = 0x3000 });
        var registry = new GpuTextureRegistry(() => [], () => []) { Held = texture => texture == held };

        Assert.Equal((false, true, true), (registry.Retires(large, held), registry.Retires(large, free), registry.Retires(again, held)));
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
