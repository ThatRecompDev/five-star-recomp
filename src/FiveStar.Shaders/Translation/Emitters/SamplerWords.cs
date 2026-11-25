// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Shaders.Translation.Emitters;

/// <summary>
/// The sampler word a shared-exponent sample filters by (<c>se_sample</c> in the prelude; the loader writes the same
/// word into a heap sampler's record): bits 0-2 / 3-5 the U / V address mode (SQ_TEX_CLAMP), 6 mag linear, 7 min
/// linear, 8-9 mip filter (0 none, 1 point, 2 linear), 10-17 max LOD and 18-25 min LOD (u4.4), 26-30 LOD bias (s3.2),
/// 31 set. Packed here from a literal S# (gfx10 SQ_IMG_SAMP_WORD0-2).
/// </summary>
internal static class SamplerWords
{
    private const int Word1 = 1;
    private const int Word2 = 2;
    private const uint AddressBits = 0x3F;
    private const int MagShift = 20;
    private const int MinShift = 22;
    private const int MipShift = 26;
    private const uint FieldMask = 3;
    private const uint LinearBit = 1;
    private const uint MipPointAniso = 3;
    private const uint MipPoint = 1;
    private const uint LodMask = 0xFFF;
    private const int MaxLodShift = 12;

    /// <summary>u4.8 to u4.4, and s5.8 to s3.2.</summary>
    private const int LodNarrow = 4;
    private const int BiasNarrow = 6;
    private const int BiasBits = 14;
    private const int SignShift = 32 - BiasBits;
    private const int BiasLow = -16;
    private const int BiasHigh = 15;
    private const uint BiasFieldMask = 0x1F;

    private const int MagBit = 6;
    private const int MinBit = 7;
    private const int MipField = 8;
    private const int MaxLodField = 10;
    private const int MinLodField = 18;
    private const int BiasField = 26;
    private const uint Known = 1u << 31;

    /// <summary>The word of a literal S# (<paramref name="words"/>: its four dwords).</summary>
    public static uint Pack(IReadOnlyList<uint> words)
    {
        uint word0 = words[0];
        uint word1 = words[Word1];
        uint word2 = words[Word2];
        uint mip = (word2 >> MipShift) & FieldMask;

        if (mip == MipPointAniso)
        {
            mip = MipPoint;
        }

        int bias = Math.Clamp((int)(word2 << SignShift) >> SignShift >> BiasNarrow, BiasLow, BiasHigh);

        return (word0 & AddressBits)
            | ((((word2 >> MagShift) & LinearBit)) << MagBit)
            | ((((word2 >> MinShift) & LinearBit)) << MinBit)
            | (mip << MipField)
            | ((((word1 >> MaxLodShift) & LodMask) >> LodNarrow) << MaxLodField)
            | (((word1 & LodMask) >> LodNarrow) << MinLodField)
            | (((uint)bias & BiasFieldMask) << BiasField)
            | Known;
    }
}
