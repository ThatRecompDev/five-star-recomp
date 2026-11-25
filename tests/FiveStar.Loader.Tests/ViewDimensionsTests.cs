// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X;
using FiveStar.Loader.Shims.D3D12X.Resources;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// A game view whose dimension does not fit its resource (the scattering distance volume: 3D image instructions on a
/// 2304x576 2D texture, all zero in w82) is named in a warning with the resource and the view desc.
/// </summary>
public sealed unsafe class ViewDimensionsTests
{
    private const int Texture2D = 3;
    private const int Texture3D = 4;
    private const int SrvTexture2D = 4;
    private const int SrvTexture3D = 8;
    private const int SrvCube = 9;
    private const int UavTexture3D = 8;
    private const int R16Float = 54;

    [Fact]
    public void A_3d_view_of_a_2d_texture_does_not_fit_and_matching_views_do()
    {
        Assert.True(ViewDimensions.Mismatch(Texture2D, SrvTexture3D, unordered: false));
        Assert.True(ViewDimensions.Mismatch(Texture2D, UavTexture3D, unordered: true));
        Assert.True(ViewDimensions.Mismatch(Texture3D, SrvTexture2D, unordered: false));

        Assert.False(ViewDimensions.Mismatch(Texture2D, SrvTexture2D, unordered: false));
        Assert.False(ViewDimensions.Mismatch(Texture3D, SrvTexture3D, unordered: false));
        Assert.False(ViewDimensions.Mismatch(Texture2D, SrvCube, unordered: false));

        // Unknown dimensions are not judged.
        Assert.False(ViewDimensions.Mismatch(Texture2D, 0, unordered: false));
        Assert.False(ViewDimensions.Mismatch(Texture2D, 42, unordered: true));
    }

    [Fact]
    public void The_warning_names_the_resource_and_the_view_desc()
    {
        var desc = new ResourceDescription { Dimension = Texture2D, Width = 2304, Height = 576, DepthOrArraySize = 1, MipLevels = 1, Format = R16Float };
        int* view = stackalloc int[] { R16Float, UavTexture3D, 0, 0, -1, 0, 0 };

        string text = ViewDimensions.Describe(0x1234, desc, view, unordered: true);

        Assert.Contains("UAV of view dimension 8 on resource 0x1234 of dimension 3 (2304x576x1, 1 mips, format 54)", text, StringComparison.Ordinal);
        Assert.Contains("view desc 00000036 00000008 00000000 00000000 ffffffff", text, StringComparison.Ordinal);
    }
}
