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
    public void A_compile_starts_only_while_memory_covers_it_what_the_running_ones_still_need_and_the_games_growth()
    {
        long mesh = CompileBudget.Estimate(3L << 20);
        long reserve = CompileBudget.PhysicalReserve(CompileBudget.GrowthAllowance(0));

        Assert.Equal(3L * 2816 << 20, mesh);
        Assert.Equal(Gigabyte / 2, CompileBudget.Estimate(4096));
        Assert.Equal(16 * Gigabyte, CompileBudget.Estimate(64L << 20));
        Assert.Equal(4 * Gigabyte, reserve);

        // 21 GB free: one running compile that still needs 8 GB leaves room for another (4 + 8 + 8.25 < 21).
        Assert.True(CompileBudget.Room(mesh, 21UL * Gigabyte, 100UL * Gigabyte, 8 * Gigabyte, reserve));

        // Two that still need 16 GB do not; nor does a commit charge near its limit.
        Assert.False(CompileBudget.Room(mesh, 21UL * Gigabyte, 100UL * Gigabyte, 16 * Gigabyte, reserve));
        Assert.False(CompileBudget.Room(mesh, 21UL * Gigabyte, 12UL * Gigabyte, 0, reserve));

        // A game that grew 9 GB in the last minute adds 4 GB (half, capped): the same compile no longer fits.
        long growing = CompileBudget.PhysicalReserve(CompileBudget.GrowthAllowance(9 * Gigabyte));
        Assert.Equal(8 * Gigabyte, growing);
        Assert.False(CompileBudget.Room(mesh, 21UL * Gigabyte, 100UL * Gigabyte, 8 * Gigabyte, growing));

        // Boot w21 at 121 s: 12.7 GB free, one heavy compile running (7 GB estimate). A 1 MB compile (2.75 GB) waits
        // unless urgent, which needs only the 4 GB floor and its own estimate.
        long resolve = CompileBudget.Estimate(1L << 20);
        Assert.False(CompileBudget.Room(resolve, 12_700UL << 20, 100UL * Gigabyte, 7 * Gigabyte, reserve));
        Assert.True(CompileBudget.Room(resolve, 12_700UL << 20, 100UL * Gigabyte, 0, CompileBudget.MinimumPhysicalReserve));
    }

    [Fact]
    public void Heavy_compiles_run_one_at_a_time_and_while_the_game_loads_only_when_urgent()
    {
        // The carry form of the foliage pipeline: 2.84 MB of DXIL.
        long foliage = 2980000;

        Assert.True(CompileBudget.HeldForLoad(foliage, urgent: false, loadWindow: true));
        Assert.False(CompileBudget.HeldForLoad(foliage, urgent: true, loadWindow: true));
        Assert.False(CompileBudget.HeldForLoad(foliage, urgent: false, loadWindow: false));
        Assert.False(CompileBudget.HeldForLoad(CompileBudget.HeavyShaderBytes, urgent: false, loadWindow: true));

        Assert.True(CompileBudget.HeavyRoom(foliage, 0));
        Assert.False(CompileBudget.HeavyRoom(foliage, 1));
        Assert.True(CompileBudget.HeavyRoom(1L << 20, 1));
    }

    [Fact]
    public void The_loader_ceilings_bound_compiles_in_flight_and_private_bytes_but_never_stop_the_first()
    {
        long small = CompileBudget.Estimate(4096);
        long ceiling = 28 * Gigabyte;
        int most = CompileBudget.DefaultMaxCompiles;

        // 20 GB private, two running that still need 4 GB: a third (0.5 GB) fits under 28 GB.
        Assert.True(CompileBudget.UnderCeiling(small, 2, 20 * Gigabyte, 4 * Gigabyte, most, ceiling));

        // Not past the most in flight, nor past the ceiling; with no ceiling (the default) private bytes do not count.
        Assert.False(CompileBudget.UnderCeiling(small, most, 10 * Gigabyte, 0, most, ceiling));
        Assert.False(CompileBudget.UnderCeiling(small, 2, 24 * Gigabyte, 4 * Gigabyte, most, ceiling));
        Assert.True(CompileBudget.UnderCeiling(small, 2, 60 * Gigabyte, 4 * Gigabyte, most, CompileBudget.DefaultCeilingBytes));

        // With none running one always starts, even a mesh compile of 8 GB at 26 GB private.
        Assert.True(CompileBudget.UnderCeiling(CompileBudget.Estimate(3L << 20), 0, 26 * Gigabyte, 0, most, ceiling));

        // Program work (boot and warm-up) waits while the compile host is over its ceiling, unless none runs.
        long host = CompileBudget.HostCeilingBytes;
        Assert.True(CompileBudget.ProgramRoom(3, 2 * Gigabyte, host));
        Assert.False(CompileBudget.ProgramRoom(3, 9 * Gigabyte, host));
        Assert.True(CompileBudget.ProgramRoom(0, 9 * Gigabyte, host));
    }
}
