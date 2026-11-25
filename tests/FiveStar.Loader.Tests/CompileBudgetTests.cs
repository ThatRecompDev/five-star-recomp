// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.Pipelines;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>The driver compile budget: estimates by DXIL size, and room for what running compiles still need.</summary>
public sealed class CompileBudgetTests
{
    private const long Gigabyte = 1L << 30;

    [Fact]
    public void A_compile_starts_only_while_memory_covers_it_and_what_the_running_ones_still_need()
    {
        long mesh = CompileBudget.Estimate(3L << 20);

        Assert.Equal(3L * 2816 << 20, mesh);
        Assert.Equal(Gigabyte / 2, CompileBudget.Estimate(4096));
        Assert.Equal(32 * Gigabyte, CompileBudget.Estimate(64L << 20));

        // The foliage mesh pipeline of 13 MB of DXIL took about 20 GB by itself.
        Assert.True(CompileBudget.Estimate(13L << 20) >= 20 * Gigabyte);

        // 21 GB free: one running compile that still needs 8 GB leaves room for another (4 + 8 + 8.25 < 21).
        Assert.True(CompileBudget.Room(mesh, 21UL * Gigabyte, 100UL * Gigabyte, 8 * Gigabyte));

        // Two that still need 16 GB do not; nor does a commit charge near its limit.
        Assert.False(CompileBudget.Room(mesh, 21UL * Gigabyte, 100UL * Gigabyte, 16 * Gigabyte));
        Assert.False(CompileBudget.Room(mesh, 21UL * Gigabyte, 12UL * Gigabyte, 0));
    }

    [Fact]
    public void The_loader_ceilings_bound_compiles_in_flight_and_private_bytes_but_never_stop_the_first()
    {
        long small = CompileBudget.Estimate(4096);
        long ceiling = CompileBudget.DefaultCeilingBytes;
        int most = CompileBudget.DefaultMaxCompiles;

        // 20 GB private, two running that still need 4 GB: a third (0.5 GB) fits under 28 GB.
        Assert.True(CompileBudget.UnderCeiling(small, 2, 20 * Gigabyte, 4 * Gigabyte, most, ceiling));

        // Not past the most in flight, nor past the ceiling.
        Assert.False(CompileBudget.UnderCeiling(small, most, 10 * Gigabyte, 0, most, ceiling));
        Assert.False(CompileBudget.UnderCeiling(small, 2, 24 * Gigabyte, 4 * Gigabyte, most, ceiling));

        // With none running one always starts, even a mesh compile of 8 GB at 26 GB private.
        Assert.True(CompileBudget.UnderCeiling(CompileBudget.Estimate(3L << 20), 0, 26 * Gigabyte, 0, most, ceiling));

        // Program work (boot and warm-up) waits while the compile host is over its ceiling, unless none runs.
        long host = CompileBudget.HostCeilingBytes;
        Assert.True(CompileBudget.ProgramRoom(3, 2 * Gigabyte, host));
        Assert.False(CompileBudget.ProgramRoom(3, 9 * Gigabyte, host));
        Assert.True(CompileBudget.ProgramRoom(0, 9 * Gigabyte, host));
    }
}
