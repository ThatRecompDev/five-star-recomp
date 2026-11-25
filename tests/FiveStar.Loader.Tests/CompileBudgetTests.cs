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
    public void AMD_estimates_grow_quadratically_and_cover_the_isolated_peaks_of_issue_19()
    {
        const uint Amd = CompileBudget.AmdVendorId;
        const uint Nvidia = 0x10DE;

        // Isolated peaks on an RX 7800 XT: 0.48 MB 1.0 GB, 1.5 MB 4.9 GB, 2.9 MB 17.2 GB, 5.0 MB over 30 GB.
        (double Megabytes, double Gigabytes)[] measured = [(0.48, 1.0), (1.5, 4.9), (2.9, 17.2), (5.0, 30)];

        foreach (var (megabytes, gigabytes) in measured)
        {
            Assert.True(CompileBudget.Estimate((long)(megabytes * (1 << 20)), Amd) > gigabytes * Gigabyte);
        }

        // NVIDIA stays linear: the carry-form foliage pipeline (2.75 MB) took 6.4 GB alone.
        Assert.Equal(CompileBudget.Estimate(3L << 20, 0), CompileBudget.Estimate(3L << 20, Nvidia));
        Assert.InRange(CompileBudget.Estimate(2_883_584, Nvidia), 6.4 * Gigabyte, 8 * Gigabyte);

        // In game the 1.25 to 2.25 MB mesh + pixel pipelines peaked at 19 to 37.5 GB on average and at most from 1.5 MB.
        Assert.InRange(CompileBudget.Estimate(3L << 19, Amd), 29 * Gigabyte, 31 * Gigabyte);
        Assert.True(CompileBudget.Estimate(7L << 18, Amd) >= 37 * Gigabyte);

        // The 12 MB ScreenProbes program would need more than a 64 GB machine has free.
        Assert.Equal(48 * Gigabyte, CompileBudget.Estimate(12L << 20, Amd));
    }

    [Fact]
    public void A_compile_past_the_machines_memory_is_recorded_unbuildable_once_per_vendor_and_driver()
    {
        // The 12 MB ScreenProbes program on AMD (48 GB) cannot build on 32 or 48 GB; it can on 64 GB.
        long probes = CompileBudget.Estimate(12L << 20, CompileBudget.AmdVendorId);
        Assert.False(CompileBudget.Buildable(probes, 32UL * Gigabyte));
        Assert.False(CompileBudget.Buildable(probes, 48UL * Gigabyte));
        Assert.True(CompileBudget.Buildable(probes, 64UL * Gigabyte));

        string path = Path.Combine(Path.GetTempPath(), $"unbuildable-{Guid.NewGuid():n}.txt");

        try
        {
            UnbuildablePipelines.Open(path);
            Assert.True(UnbuildablePipelines.Add("abc"));
            Assert.False(UnbuildablePipelines.Add("abc"));

            // A record of another driver does not count; this adapter's survives a new boot.
            File.AppendAllText(path, UnbuildablePipelines.Record("def", 0x10DE, 1) + Environment.NewLine);
            UnbuildablePipelines.Open(path);
            Assert.Equal(1, UnbuildablePipelines.Count);
        }
        finally
        {
            UnbuildablePipelines.Open(null);
            File.Delete(path);
        }
    }

    [Fact]
    public void A_running_compile_keeps_its_whole_estimate_reserved_only_until_it_settles()
    {
        long estimate = CompileBudget.Estimate(2_800_000);

        Assert.Equal(estimate, CompileBudget.Reservation(estimate, 10, 0));
        Assert.Equal(4 * Gigabyte, CompileBudget.Reservation(estimate, 135, 4 * Gigabyte));
        Assert.Equal(estimate / 4, CompileBudget.Reservation(estimate, 135, -Gigabyte));
        Assert.Equal(estimate, CompileBudget.Reservation(estimate, 135, 20 * Gigabyte));

        // Boot w32 at 153 s: 14.1 GB free, 15 GB of commit left, the 2 MB compile 73 s in while the process grew 4 GB.
        // A 1 MB compile now fits (5 + 4 + 2.75 GB physical, 8 + 4 + 2.75 GB commit); with the whole 7.5 GB it did not.
        long resolve = CompileBudget.Estimate(1L << 20);
        long reserve = CompileBudget.PhysicalReserve(CompileBudget.GrowthAllowance(2 * Gigabyte));
        Assert.True(CompileBudget.Room(resolve, 14_100UL << 20, 15UL * Gigabyte, CompileBudget.Reservation(estimate, 73, 4 * Gigabyte), reserve));
        Assert.False(CompileBudget.Room(resolve, 14_100UL << 20, 15UL * Gigabyte, estimate, reserve));
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
