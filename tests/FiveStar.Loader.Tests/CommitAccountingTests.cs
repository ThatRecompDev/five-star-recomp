// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Diagnostics;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>The sample line's commit by category: what no category names is the driver's and native memory.</summary>
public sealed class CommitAccountingTests
{
    private const long Gigabyte = 1L << 30;

    [Fact]
    public void Private_bytes_split_into_the_video_memory_mirror_managed_game_memory_and_the_rest()
    {
        var figures = new CommitAccounting.Figures(
            Private: 40 * Gigabyte,
            WriteCombined: 12 * Gigabyte,
            PlacedTextureHeaps: 9 * Gigabyte,
            Managed: 2 * Gigabyte,
            GameMemory: 6 * Gigabyte,
            CompileArenasExpected: 8 * Gigabyte,
            XMemPages: 14 * Gigabyte,
            SectionHeaps: 3 * Gigabyte,
            Helpers: Gigabyte / 2);

        Assert.Equal(
            "commit by category: private 40.0 GB = video memory mirror 12.0 GB (placed texture heaps 9.0 GB, other 3.0 GB) + managed 2.0 GB + game memory 6.0 GB + driver and native 20.0 GB (compile arenas expected 8.0 GB); outside it XMem pages 14.0 GB (section heaps open over 3.0 GB of them), helper processes 0.5 GB",
            CommitAccounting.Format(figures));
    }
}
