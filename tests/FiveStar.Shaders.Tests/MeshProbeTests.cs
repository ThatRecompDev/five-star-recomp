// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Tests.Cases;
using FiveStar.Shaders.Tests.Toolchain;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// The mesh probe (loader --probe-pipeline) on the two-phase NGG program with four loads in front: the group draws the
/// same image, and its record holds the counts it gave SetMeshOutputCounts, its culled primitive, vertex 0's position,
/// and per load site whether its range check failed, it read an unmapped page or a record naming no descriptor.
/// </summary>
public sealed class MeshProbeTests : IClassFixture<WarpFixture>
{
    private const uint Id = 0x5EED;

    private readonly WarpFixture _fixture;

    public MeshProbeTests(WarpFixture fixture)
    {
        _fixture = fixture;
    }

    [WarpFact]
    public void A_probed_mesh_group_records_its_counts_culling_position_and_failed_loads()
    {
        var (mismatches, records, sites) = new MeshStageRunner(_fixture).RunProbed(Id);
        int unmappedBuffer = Site(sites, "buf_fits", 0);
        int goodBuffer = Site(sites, "buf_fits", 1);
        int pointer = Site(sites, "PtrLoad", 0);
        int nullRecord = Site(sites, "buf_fits", 2);

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Take(8)));
        var record = Assert.Single(records);

        Assert.Equal(Id, record.Id);
        Assert.Equal((18u, 16u), (record.Vertices, record.Primitives));
        Assert.Equal((1u, 1u << 5), (record.Culled, record.CullMask));
        Assert.Equal((-1f, 1f, 0f, 1f), record.Position);

        Assert.Equal(Bit(unmappedBuffer), record.RangeFailed);
        Assert.Equal(Bit(unmappedBuffer) | Bit(pointer), record.Unmapped);
        Assert.Equal(Bit(nullRecord), record.NullRecord);
        Assert.Equal(0u, (record.RangeFailed | record.Unmapped | record.NullRecord) & Bit(goodBuffer));
    }

    /// <summary>The number of the <paramref name="nth"/> site whose text has <paramref name="call"/> (a buffer load's site is its check, buf_fits).</summary>
    private static int Site(IReadOnlyList<string> sites, string call, int nth)
    {
        var matching = sites.Select((text, index) => (text, index)).Where(site => site.text.Contains(call, StringComparison.Ordinal)).ToList();

        return matching[nth].index;
    }

    private static uint Bit(int site)
    {
        return 1u << Math.Min(site, 31);
    }
}
