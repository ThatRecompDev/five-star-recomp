// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// --watch-4g-lines: the 4 GiB lines inside the game's GPU-writable heap are found, and an index array written over
/// records past one (as at world load: 0x3000c, 0x3000d at +0x30) is told from an ordinary change.
/// </summary>
public sealed class FourGiBLineWatchTests
{
    [Fact]
    public void The_lines_inside_an_allocation_are_the_4_GiB_steps_of_its_high_dword()
    {
        // w62's GPU-writable heap: 0x49400000 bytes at 0x156ffdb0000, the overwritten object at 0x15700000030.
        Assert.Equal([0x15700000000UL], LineChanges.Lines(0x156ffdb0000, 0x49400000));
        Assert.Empty(LineChanges.Lines(0x15700000000, 0x1000));
        Assert.Equal([0x200000000UL, 0x300000000UL], LineChanges.Lines(0x1ffff0000, 0x100010001));
    }

    [Fact]
    public void An_index_array_over_records_is_flagged_and_an_ordinary_change_is_not()
    {
        var before = new byte[0x80];
        var after = (byte[])before.Clone();

        for (int i = 0xc; i < 0x10; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(after.AsSpan(i * 4), 0x30000u + (uint)i);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(after.AsSpan(0x70), 0x1234);
        var runs = LineChanges.Changed(before, after);

        Assert.Equal([(0x30, 16), (0x70, 4)], runs);
        Assert.EndsWith("(4 consecutive values from 0x3000c: an index array)", LineChanges.Describe(before, after, runs[0], 64), StringComparison.Ordinal);
        Assert.DoesNotContain("index array", LineChanges.Describe(before, after, runs[1], 64), StringComparison.Ordinal);
    }
}
