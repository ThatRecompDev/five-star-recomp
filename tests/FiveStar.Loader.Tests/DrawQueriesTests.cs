// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// The draw trace's hardware counters are read in D3D12_QUERY_DATA_PIPELINE_STATISTICS1 order, with the occlusion count
/// resolved after them: a mesh pass that runs its groups but hands the rasterizer nothing reads as MS > 0, MS primitives 0.
/// </summary>
public sealed class DrawQueriesTests
{
    [Fact]
    public void Counters_are_named_in_the_statistics_order_and_the_samples_come_last()
    {
        // IAVertices, IAPrimitives, VS, GS, GS primitives, C invocations, C primitives, PS, HS, DS, CS, AS, MS, MS primitives; samples.
        ulong[] counts = [100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114];

        Assert.Equal(DrawQueries.ResultCounters, counts.Length);
        Assert.Equal(
            "AS 111 MS 112 MS primitives 113, clipper in 105 out 106, PS 107, VS 102 IA primitives 101, CS 110; samples through depth and stencil 114",
            DrawQueries.Describe(counts));
    }
}
