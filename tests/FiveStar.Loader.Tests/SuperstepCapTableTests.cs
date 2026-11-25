// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Shims.D3D12X.Binding;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// The superstep cap table after the window guard's: programs report threads past the block cap and groups past the
/// round bound per program slot, and each report names only what grew since the last.
/// </summary>
public sealed unsafe class SuperstepCapTableTests
{
    private const int Slots = 1024;
    private const int SlotDwords = 4;

    [Fact]
    public void Each_report_names_the_programs_whose_cap_counts_grew_since_the_last()
    {
        uint* table = (uint*)NativeMemory.AllocZeroed(Slots * SlotDwords * sizeof(uint));
        var (blocks, rounds) = (new uint[Slots], new uint[Slots]);

        try
        {
            // LinearDepthSkin's id in its slot: 3,456 threads ran out of blocks; another program ran out of rounds twice.
            Write(table, 0x5EED, 3456, 0);
            Write(table, 0x0123, 0, 2);

            Assert.Equal([(0x0123u, 0u, 2u), (0x5EEDu, 3456u, 0u)], WindowGuard.CapHits(table, blocks, rounds));

            // Nothing new: no report. Then 128 more threads of the first.
            Assert.Empty(WindowGuard.CapHits(table, blocks, rounds));
            Write(table, 0x5EED, 3456 + 128, 0);
            Assert.Equal([(0x5EEDu, 128u, 0u)], WindowGuard.CapHits(table, blocks, rounds));

            Assert.Contains($"#define {WindowGuard.CapDefine} {WindowGuard.Base + WindowGuard.CapOffset}u", WindowGuard.Header, StringComparison.Ordinal);
            Assert.True(WindowGuard.CapOffset + (Slots * SlotDwords * sizeof(uint)) <= WindowGuard.RegionBytes);
        }
        finally
        {
            NativeMemory.Free(table);
        }
    }

    /// <summary>The slot of <paramref name="id"/> (id &amp; 1023) as a program leaves it.</summary>
    private static void Write(uint* table, uint id, uint blocks, uint rounds)
    {
        uint* slot = table + ((id & (Slots - 1)) * SlotDwords);
        (slot[0], slot[1], slot[2]) = (id, blocks, rounds);
    }
}
