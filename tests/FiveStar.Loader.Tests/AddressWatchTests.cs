// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.Resources;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>`--watch-address`: the blocks of the watched bytes are counted as pure magenta BC1, zero, or other.</summary>
public sealed unsafe class AddressWatchTests
{
    [Fact]
    public void Magenta_zero_and_other_blocks_are_told_apart()
    {
        ulong[] blocks = [0xFFFFFFFF_F81FF81FUL, 0UL, 0x00000000_F81FF81FUL, 0xAAAAAAAA_FFFF0000UL];

        fixed (ulong* data = blocks)
        {
            var (magenta, zero, other) = AddressWatch.Count((byte*)data, (ulong)(blocks.Length * sizeof(ulong)));

            Assert.Equal(2, magenta);
            Assert.Equal(1, zero);
            Assert.Equal(1, other);
        }
    }
}
