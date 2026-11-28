// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.Pipelines;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>`--log-skips`: the stand-ins named by a part of their name, case-insensitive, are logged; an empty list turns it off.</summary>
public sealed class SkipLogTests
{
    [Fact]
    public void Stand_ins_are_matched_by_a_part_of_their_name()
    {
        try
        {
            SkipLog.Configure("bakedecorations, texturecompression~");

            Assert.True(SkipLog.Enabled);
            Assert.True(SkipLog.Matches("sgd1_0614c9c0_t32_PS_BakeDecorations_0000_b1e4d949_Wrapped"));
            Assert.False(SkipLog.Matches("sgd1_06e95600_t32_PS_DeferredTextured_942fcba4_Wrapped"));
        }
        finally
        {
            SkipLog.Configure(string.Empty);
        }

        Assert.False(SkipLog.Enabled);
    }
}
