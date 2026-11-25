// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.Resources;

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// <c>--typed-image-views</c>: a heap sampler's record carries its filtering in dword 3, which a translated program reads
/// when it filters shared-exponent light itself (FiveStar.Shaders SamplerWords, <c>se_sample</c>): bits 0-2 / 3-5 the U /
/// V address mode (SQ_TEX_CLAMP), 6 mag linear, 7 min linear, 8-9 mip filter (1 point, 2 linear), 10-17 max LOD and
/// 18-25 min LOD (u4.4), 26-30 LOD bias (s3.2), 31 set.
/// </summary>
internal static unsafe class SamplerRecords
{
    /// <summary>D3D12_FILTER: the mip, mag and min linear bits.</summary>
    private const int MipLinear = 0x1;
    private const int MagLinear = 0x4;
    private const int MinLinear = 0x10;

    /// <summary>D3D12_TEXTURE_ADDRESS_MODE WRAP..MIRROR_ONCE as SQ_TEX_CLAMP: wrap, mirror, clamp to the last texel, border, mirror once.</summary>
    private static readonly uint[] AddressModes = [0, 0, 1, 2, 6, 3];

    private const int AddressV = 3;
    private const uint ClampToEdge = 2;
    private const int MagBit = 6;
    private const int MinBit = 7;
    private const int MipField = 8;
    private const uint MipPoint = 1;
    private const uint MipLinearFilter = 2;
    private const int MaxLodField = 10;
    private const int MinLodField = 18;
    private const int BiasField = 26;
    private const float LodScale = 16f;
    private const float LodHigh = 255f;
    private const float BiasScale = 4f;
    private const int BiasLow = -16;
    private const int BiasHigh = 15;
    private const uint BiasMask = 0x1F;
    private const uint Known = 1u << 31;

    /// <summary>The record of a sampler made from <paramref name="description"/>; no fields when the option is off.</summary>
    public static BufferRecord Of(DesktopSamplerDescription* description)
    {
        if (!ImageViewClasses.Enabled || description == null)
        {
            return BufferRecord.None;
        }

        return BufferRecord.None with { ImageClass = Word(*description) };
    }

    /// <summary>The sampler word of a D3D12_SAMPLER_DESC.</summary>
    public static uint Word(DesktopSamplerDescription description)
    {
        int filter = description.Filter;
        uint mip = (filter & MipLinear) != 0 ? MipLinearFilter : MipPoint;
        int bias = Math.Clamp((int)MathF.Round(description.MipLodBias * BiasScale), BiasLow, BiasHigh);

        uint mag = (uint)(filter & MagLinear) / MagLinear;
        uint min = (uint)(filter & MinLinear) / MinLinear;

        return Address(description.AddressU)
            | (Address(description.AddressV) << AddressV)
            | (mag << MagBit)
            | (min << MinBit)
            | (mip << MipField)
            | (Lod(description.MaxLod) << MaxLodField)
            | (Lod(description.MinLod) << MinLodField)
            | (((uint)bias & BiasMask) << BiasField)
            | Known;
    }

    private static uint Address(int mode)
    {
        if (mode <= 0 || mode >= AddressModes.Length)
        {
            return ClampToEdge;
        }

        return AddressModes[mode];
    }

    private static uint Lod(float lod)
    {
        return (uint)Math.Clamp(lod * LodScale, 0f, LodHigh);
    }
}
