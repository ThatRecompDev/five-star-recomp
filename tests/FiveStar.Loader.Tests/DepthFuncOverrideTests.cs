// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.Pipelines;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary><c>--depth-func-override</c>: a named pipeline's depth comparison is replaced and its stencil test turned off.</summary>
public sealed class DepthFuncOverrideTests
{
    [Fact]
    public void A_matching_pipeline_gets_the_comparison_and_no_stencil_test()
    {
        Assert.Null(DepthFuncOverride.Parse("TL_PS=Sometimes"));
        var rules = DepthFuncOverride.Parse("TL_PS_NoDir=Always; SSS=lessequal");
        Assert.Equal([("TL_PS_NoDir", 8), ("SSS", 4)], rules);

        // TL_PS's state: depth on, no writes, EQUAL (3), stencil on (to see it turned off).
        byte[] state = new byte[52];
        state[0] = 1;
        state[8] = 3;
        state[12] = 1;

        DepthFuncOverride.Rules = rules!;
        byte[] changed = DepthFuncOverride.Apply(state, "sgd1_0b1e1c88_t32_TL_PS_NoDir_PBR_MATERIAL_STANDARD + VS_TiledLighting");
        Assert.Equal(8, BitConverter.ToInt32(changed, 8));
        Assert.Equal(0, BitConverter.ToInt32(changed, 12));
        Assert.Equal(3, state[8]);

        Assert.Same(state, DepthFuncOverride.Apply(state, "PS_DeferredTextured"));
        DepthFuncOverride.Rules = [];
    }
}
