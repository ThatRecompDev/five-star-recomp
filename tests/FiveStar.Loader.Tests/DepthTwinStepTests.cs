// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// The R16_UNORM depth twin's copy into the depth plane (DepthTwinCopy: round(twin * 65535) * asfloat(0x37800080))
/// gives, for every one of the 65,536 steps, the very float VS_TiledLighting draws that material class at
/// (asfloat(0x37800080) * float(class)), so the lighting's depth EQUAL holds bit for bit.
/// </summary>
public sealed class DepthTwinStepTests
{
    private const int Steps = 65536;
    private const float UnormMax = 65535f;

    [Fact]
    public void Every_unorm_step_copies_into_the_float_the_lighting_draws_its_class_at()
    {
        float step = BitConverter.UInt32BitsToSingle(0x37800080);
        var seen = new HashSet<uint>();

        for (int c = 0; c < Steps; c++)
        {
            // What the GPU reads from the UNORM twin (c / 65535, correctly rounded) and what the copy writes.
            float unorm = c / UnormMax;
            float copied = MathF.Round(unorm * UnormMax) * step;
            float drawn = step * c;

            Assert.Equal(BitConverter.SingleToUInt32Bits(drawn), BitConverter.SingleToUInt32Bits(copied));
            Assert.True(seen.Add(BitConverter.SingleToUInt32Bits(copied)));
        }

        // The classes the w187 dump found in TL_PS's depth: 5 and 6 steps.
        Assert.Equal(9.15541E-05f, step * 6, 9);
    }
}
