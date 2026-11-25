// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Shims.D3D12X.Resources;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>The filtering a heap sampler's record carries for the programs that filter shared-exponent light themselves.</summary>
public sealed class SamplerRecordsTests
{
    [Fact]
    public void A_trilinear_clamped_sampler_packs_as_the_prelude_reads_it()
    {
        // MIN_MAG_MIP_LINEAR, CLAMP on U and V, LODs 0 to 15: U/V mode 2, mag and min linear, linear mips, max LOD 240/16.
        var trilinear = new DesktopSamplerDescription { Filter = 0x15, AddressU = 3, AddressV = 3, AddressW = 3, MaxLod = 15 };

        Assert.Equal(0x8003C2D2u, SamplerRecords.Word(trilinear));

        // MIN_MAG_MIP_POINT, WRAP, a bias of -1: point everywhere, mode 0, bias -4 quarters in bits 26-30.
        var point = new DesktopSamplerDescription { Filter = 0, AddressU = 1, AddressV = 1, MipLodBias = -1, MaxLod = 1 };

        Assert.Equal(0x80000000u | (0x1Cu << 26) | (16u << 10) | (1u << 8), SamplerRecords.Word(point));
    }
}
