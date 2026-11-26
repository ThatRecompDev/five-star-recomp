// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Mods;
using FiveStar.Loader.Native;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>--output WxH: the stub that sets the title's output size, and its place near the image.</summary>
public sealed unsafe class OutputSizePatchesTests
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const int JumpBytes = 5;
    private const int ImageBytes = 1 << 20;
    private const int RegionBytes = 2 << 20;
    private const int StepBytes = 1 << 16;

    [Fact]
    public void The_stub_writes_the_size_into_the_settings_and_eax()
    {
        byte[] expected =
        [
            0xC7, 0x86, 0x04, 0x02, 0x00, 0x00, 0x80, 0x07, 0x00, 0x00,
            0xC7, 0x86, 0x08, 0x02, 0x00, 0x00, 0x38, 0x04, 0x00, 0x00,
            0xB8, 0x80, 0x07, 0x00, 0x00,
        ];

        Assert.Equal(expected, OutputSizePatches.Stub(Width, Height));
    }

    [Fact]
    public void Only_the_titles_modes_are_taken_and_4k_keeps_the_pool_check()
    {
        Assert.True(OutputSizePatches.IsMode(Width, Height));
        Assert.False(OutputSizePatches.IsMode(1600, 900));
        Assert.Equal(2, OutputSizePatches.For(Width, Height).Count);
        Assert.Single(OutputSizePatches.For(3840, 2160));
    }

    [Fact]
    public void A_stub_goes_within_a_rel32_jump_below_the_image_and_jumps_back()
    {
        // A stand-in image of our own: a region reserved whole, then its lower half let go, so the 64 KiB step just
        // below the image is free whatever else the process mapped (a heap block's neighbours were not, and the test
        // failed now and then in a full run).
        nint region = Kernel32.VirtualAlloc(0, RegionBytes, Win32Constants.MemReserve, Win32Constants.PageNoAccess);
        Assert.NotEqual(0, region);
        Assert.NotEqual(0, Kernel32.VirtualFree(region, 0, Win32Constants.MemRelease));

        nint image = Kernel32.VirtualAlloc(region + ImageBytes, ImageBytes, Win32Constants.MemReserve, Win32Constants.PageNoAccess);
        Assert.Equal(region + ImageBytes, image);
        nint stub = 0;

        try
        {
            byte[] body = [0x90, 0x90, 0x90];
            nint next = image + 0x1234;
            stub = NearStubs.Place(image, body, next);

            Assert.Equal(image - StepBytes, stub);
            var placed = new ReadOnlySpan<byte>((void*)stub, body.Length + JumpBytes).ToArray();
            Assert.Equal(body, placed[..body.Length]);
            Assert.Equal(NearStubs.Jump(stub + body.Length, next), placed[body.Length..]);
            Assert.Null(NearStubs.Jump(0, unchecked((nint)0x7FFF_0000_0000)));
        }
        finally
        {
            _ = Kernel32.VirtualFree(image, 0, Win32Constants.MemRelease);

            if (stub != 0)
            {
                _ = Kernel32.VirtualFree(stub, 0, Win32Constants.MemRelease);
            }
        }
    }
}
