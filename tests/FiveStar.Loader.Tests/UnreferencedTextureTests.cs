// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Shims.D3D12X.Resources;
using FiveStar.Loader.Shims.Xg;
using FiveStar.Loader.Tests.Support;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// Textures over unmapped memory are retired once no view record names their views: the records each heap index has,
/// the handles each texture's views sit at, and the retirement with its null views.
/// </summary>
public sealed unsafe class UnreferencedTextureTests
{
    private const ulong TextureBytes = 0x1000;
    private const int Size = 64;
    private const int Rgba8Unorm = 28;
    private const int Texture2D = 3;

    [Fact]
    public void A_heap_index_is_referenced_while_a_view_record_names_it()
    {
        using var records = new WindowRecordIndices();

        records.Written(10, 4321, 0, 0, 0);
        records.Written(11, 4321, 0, 0, 0);
        Assert.True(records.Referenced(4321));

        // One record names another index, the other a V# (console fields set): none names 4321.
        records.Written(10, 99, 0, 0, 0);
        Assert.True(records.Referenced(4321));
        records.Written(11, 4321, 0x284, 0x10, 0);

        Assert.False(records.Referenced(4321));
        Assert.True(records.Referenced(99));
        Assert.False(records.Referenced(WindowRecordIndices.None));
    }

    [Fact]
    public void A_textures_views_follow_writes_copies_and_rewrites()
    {
        using var test = TestRuntime.Create();
        var device = new XboxDevice(test);
        var views = new TextureViewHandles();
        var texture = Texture(test, device, 0x10000);

        views.Written(0x1000, texture, 5);
        views.Copied(0x2000, 0x1000, 2, 0x20);

        // The copy's second descriptor came from a handle holding nothing known.
        Assert.Equal([(0x1000, 5), (0x2000, 5)], views.Of(texture).OrderBy(view => view.Handle));

        views.Forget(0x1000);
        Assert.Equal([(0x2000, 5)], views.Of(texture));

        views.Drop(texture);
        Assert.Empty(views.Of(texture));
    }

    [Fact]
    public void A_texture_over_unmapped_memory_no_record_names_is_retired_once_with_its_views_nulled()
    {
        using var test = TestRuntime.Create();
        var device = new XboxDevice(test);
        var registry = new GpuTextureRegistry(() => [], () => []);
        bool referenced = true;
        var retired = new List<GpuTexture>();

        registry.Referenced = _ => referenced;
        registry.Retiring = retired.AddRange;

        var texture = Texture(test, device, 0x100000);
        registry.Register(texture);
        Assert.Equal(1, registry.Unmapped(0x100000, 0x10000));

        // Referenced: kept however long its memory stays unmapped.
        registry.Advance(GpuTextureRegistry.UnreferencedUnmappedFrames * 2);
        Assert.NotNull(registry.Find(0x100000));

        // No record names it any more: kept for UnreferencedFrames presents, then retired, its views nulled once.
        referenced = false;
        long from = (GpuTextureRegistry.UnreferencedUnmappedFrames * 2) + 1;
        registry.Advance(from);
        registry.Advance(from + GpuTextureRegistry.UnreferencedFrames - 1);
        Assert.NotNull(registry.Find(0x100000));

        registry.Advance(from + GpuTextureRegistry.UnreferencedFrames);
        Assert.Null(registry.Find(0x100000));
        Assert.Equal([texture], retired);

        registry.Advance(from + GpuTextureRegistry.UnreferencedFrames + GpuTextureRegistry.GraceFrames);
        Assert.Equal(1, registry.Released);
        Assert.Single(retired);
        registry.Dispose();
    }

    [Fact]
    public void Without_the_record_check_no_texture_is_retired_so()
    {
        using var test = TestRuntime.Create();
        var device = new XboxDevice(test);
        var registry = new GpuTextureRegistry(() => [], () => []);
        registry.Register(Texture(test, device, 0x100000));
        registry.Unmapped(0x100000, 0x10000);

        registry.Advance(GpuTextureRegistry.UnreferencedUnmappedFrames * 4);

        Assert.NotNull(registry.Find(0x100000));
        registry.Dispose();
    }

    [Theory]
    [InlineData(4, 4, 1, 0)]
    [InlineData(5, 5, 1, 1)]
    [InlineData(10, 10, 1, 1)]
    [InlineData(6, 6, 0, 0)]
    [InlineData(99, 4, 1, 0)]
    public void A_null_view_keeps_the_dimension_it_replaces(int dimension, int expected, int mips, int arraySize)
    {
        int* desc = stackalloc int[10];

        GpuTextures.NullViewDescription(desc, dimension);

        Assert.Equal((Rgba8Unorm, expected, 0x1688), (desc[0], desc[1], desc[2]));
        Assert.Equal((mips, arraySize), (desc[5], desc[7]));
    }

    /// <summary>A registry entry over 0x1000 bytes at <paramref name="address"/>, holding its own reference on a desktop buffer.</summary>
    private static GpuTexture Texture(TestRuntime test, XboxDevice device, ulong address)
    {
        nint desktop = test.Runtime.D3D12X.Objects.Unwrap(device.Buffer(1, Size, 0));
        _ = ((delegate* unmanaged<nint, uint>)XboxDevice.Vtable(desktop)[1])(desktop);
        var description = new ResourceDescription { Dimension = Texture2D, Width = Size, Height = Size, DepthOrArraySize = 1, MipLevels = 1, Format = Rgba8Unorm, SampleCount = 1 };

        return new GpuTexture(address, description, desktop, Rgba8Unorm, 0, false, new XgSurface { Size = TextureBytes });
    }
}
