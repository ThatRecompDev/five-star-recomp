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
    public void Unmapping_and_mapping_mark_exactly_the_textures_over_the_range()
    {
        var random = new Random(11);
        var textures = Layout(random);
        var registry = new GpuTextureRegistry(() => [], () => []);

        foreach (var texture in textures.Reverse())
        {
            registry.Register(texture);
        }

        var expected = new bool[textures.Length];

        for (int i = 0; i < Ranges; i++)
        {
            ulong address = (ulong)random.Next(0, Textures * 8) * Page / 2;
            ulong bytes = (ulong)random.Next(1, 6) * Page;
            bool unmap = random.Next(2) == 0;
            int changes = 0;

            for (int index = 0; index < textures.Length; index++)
            {
                var texture = textures[index];

                if (texture.Address < address + bytes && address < texture.Address + texture.Size && expected[index] != unmap)
                {
                    expected[index] = unmap;
                    changes++;
                }
            }

            if (unmap)
            {
                Assert.Equal(changes, registry.Unmapped(address, bytes));
            }
            else
            {
                registry.Mapped(address, bytes);
            }

            Assert.Equal(expected, textures.Select(texture => texture.OverUnmapped).ToArray());
        }
    }

    [Fact]
    public void Registrations_past_many_rebuilds_find_what_a_scan_of_the_live_textures_finds()
    {
        var random = new Random(13);
        var registry = new GpuTextureRegistry(() => [], () => []);
        var live = new List<GpuTexture>();

        // Overlapping registrations retire what they cover, past several rebuilds of the large array.
        for (int i = 0; i < TextureRanges.RecentLimit * 6; i++)
        {
            ulong address = (ulong)random.Next(0, Textures * 4) * Page;
            ulong size = (ulong)random.Next(1, 5) * Page;
            var texture = new GpuTexture(address, default(ResourceDescription), 0, 0, 0, false, new XgSurface { Size = size });
            live.RemoveAll(entry => entry.Address < address + size && address < entry.Address + entry.Size);
            live.Add(texture);
            registry.Register(texture);

            Assert.Equal(live.Count, registry.Count);
            ulong probe = (ulong)random.Next(0, Textures * 8) * Page / 2;
            Assert.Same(live.SingleOrDefault(entry => entry.Contains(probe)), registry.Find(probe));

            var expected = live.Where(entry => entry.Address < probe + (2 * Page) && probe < entry.Address + entry.Size).OrderBy(entry => entry.Address);
            Assert.Equal(expected, registry.Overlapping(probe, 2 * Page));
        }
    }

    [Fact]
    public void Removed_textures_are_not_found_before_or_after_a_rebuild()
    {
        var ranges = new TextureRanges();
        var textures = Layout(new Random(17));

        foreach (var texture in textures)
        {
            ranges.Add(texture, []);
        }

        var removed = textures.Where((_, index) => index % 3 == 0).ToHashSet();
        ranges.Remove(removed);

        foreach (var texture in textures)
        {
            if (removed.Contains(texture))
            {
                Assert.Null(ranges.Find(texture.Address));
            }
            else
            {
                Assert.Same(texture, ranges.Find(texture.Address));
            }
        }

        ranges.Rebuild();
        Assert.Equal(textures.Length - removed.Count, ranges.Count);
        Assert.Equal(textures.Where(texture => !removed.Contains(texture)), ranges.All);
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
