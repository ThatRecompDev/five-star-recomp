// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.Pipelines;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>The tiled lighting's EQUAL class test becomes GREATER_EQUAL with a depth-bounds floor below its class's float.</summary>
public sealed class ClassDepthBoundsTests
{
    private const string TiledLighting = "sgd1_0b1e1c88_t32_TL_PS_NoDir_PBR_MATERIAL_STANDARD + sgd1_04650ba8_t32_VS_TiledLighting_fdbb4ddc_Wrapped";

    [Fact]
    public void An_equal_class_test_gets_greater_equal_and_the_bounds_test()
    {
        // TL_PS's state as the game gives it (D3D12_DEPTH_STENCIL_DESC1): depth on, no writes, EQUAL.
        byte[] state = new byte[56];
        state[0] = 1;
        state[8] = 3;

        byte[] changed = ClassDepthBounds.Apply(state, TiledLighting);
        Assert.Equal(7, BitConverter.ToInt32(changed, 8));
        Assert.Equal(1, BitConverter.ToInt32(changed, 0x34));
        Assert.Same(state, ClassDepthBounds.Apply(state, "PS_DeferredTextured + VS_TransformD"));

        state[4] = 1;
        Assert.Same(state, ClassDepthBounds.Apply(state, TiledLighting));
    }

    [Fact]
    public void A_class_floor_keeps_its_float_and_drops_the_lower_classes()
    {
        float step = BitConverter.UInt32BitsToSingle(0x37800080);

        foreach (uint classIndex in new uint[] { 0, 1, 6, 30, 89 })
        {
            var (min, max) = ClassDepthBounds.Bounds(classIndex);
            Assert.InRange(classIndex * step, min, max);
            Assert.Equal(1f, max);
            Assert.False(classIndex > 0 && (classIndex - 1) * step >= min);
        }
    }
}
