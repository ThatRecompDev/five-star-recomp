// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.Binding;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// The window's view records by the heap index they name, kept beside the write-combined window: the per-draw scans
/// visit only records naming a watched index, those written before the watch began and after it alike.
/// </summary>
public sealed class WindowRecordIndicesTests
{
    private const uint Watched = 5000;
    private const uint Other = 6000;
    private const uint BufferWord = 0x1234;

    [Fact]
    public void Only_records_naming_a_watched_index_are_visited()
    {
        using var records = new WindowRecordIndices();
        records.Written(10, Watched, 0, 0, 0);
        records.Written(11, Other, 0, 0, 0);
        records.Written(12, Watched, BufferWord, 0, 0);

        Assert.False(records.Any);
        Assert.Equal(Watched, records.HeapIndex(10));
        Assert.Null(records.HeapIndex(12));

        records.Watch(Watched);
        records.Written(70, Watched, 0, 0, 0);

        Assert.Equal(new uint[] { 10, 70 }, Visit(records, 0, 100));
        Assert.Equal(new uint[] { 70 }, Visit(records, 11, 100));
        Assert.Empty(Visit(records, 11, 70));

        // A record written again with another index leaves the watched ones.
        records.Written(10, Other, 0, 0, 0);
        Assert.Equal(new uint[] { 70 }, Visit(records, 0, 100));
    }

    private static List<uint> Visit(WindowRecordIndices records, uint first, uint end)
    {
        var visited = new List<uint>();

        for (uint slot = records.NextMarked(first, end); slot < end; slot = records.NextMarked(slot + 1, end))
        {
            visited.Add(slot);
        }

        return visited;
    }
}
