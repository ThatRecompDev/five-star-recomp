// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X;
using FiveStar.Loader.Shims.D3D12X.Resources;
using FiveStar.Loader.Shims.Xg;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// Registering a texture finds the textures its range overlaps by binary search, a run of the sorted array (registered
/// ranges never overlap), instead of copying and sorting the whole array: the run matches a scan of every texture.
/// </summary>
public sealed class GpuTextureRegistryRunTests
{
    private const int Textures = 200;
    private const int Ranges = 2000;
    private const ulong Page = 0x1000;

    [Fact]
    public void The_overlap_run_is_every_texture_a_scan_finds_and_its_place_keeps_the_order()
    {
        var random = new Random(7);
        var sorted = Layout(random);

        for (int i = 0; i < Ranges; i++)
        {
            ulong address = (ulong)random.Next(0, Textures * 8) * Page / 2;
            ulong bytes = (ulong)random.Next(0, 6) * Page;
            var (first, count) = GpuTextureRegistry.OverlapRun(sorted, address, bytes);

            var scanned = Enumerable.Range(0, sorted.Length)
                .Where(index => sorted[index].Address < address + bytes && address < sorted[index].Address + sorted[index].Size)
                .ToArray();

            Assert.Equal(scanned, Enumerable.Range(first, count).ToArray());
            Assert.True(first == 0 || sorted[first - 1].Address <= address, $"range 0x{address:x} + 0x{bytes:x} placed after a later texture");
            Assert.True(first + count == sorted.Length || sorted[first + count].Address >= address + bytes || bytes == 0, $"range 0x{address:x} + 0x{bytes:x} placed before an earlier texture");
        }
    }

    [Fact]
    public void An_empty_registry_places_a_texture_first()
    {
        Assert.Equal((0, 0), GpuTextureRegistry.OverlapRun([], 0x10000, Page));
    }

    /// <summary>Non-overlapping textures of one to three pages, with gaps, sorted by address.</summary>
    private static GpuTexture[] Layout(Random random)
    {
        var textures = new List<GpuTexture>();
        ulong address = 0;

        for (int i = 0; i < Textures; i++)
        {
            address += (ulong)random.Next(0, 3) * Page;
            ulong size = (ulong)random.Next(1, 4) * Page;
            textures.Add(new GpuTexture(address, default(ResourceDescription), 0, 0, 0, false, new XgSurface { Size = size }));
            address += size;
        }

        return [.. textures];
    }
}
