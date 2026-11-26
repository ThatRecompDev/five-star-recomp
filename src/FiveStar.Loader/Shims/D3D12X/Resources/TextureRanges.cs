// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// The registered textures by address, for <see cref="GpuTextureRegistry"/>: lookups read without a lock, changes are
/// made under the registry's lock. Two runs sorted by start: a large one rebuilt now and then and a small one of the
/// textures registered since. A registration copies only the small one; a retired texture of the large one is flagged
/// (<see cref="GpuTexture.Unregistered"/>) and dropped at the next rebuild. #56's registration copied the whole array
/// (prefix, texture, suffix) each time: with about 20,000 textures w126 allocated 1.4 GB of arrays in 20 s that way, 81%
/// of the managed allocations.
/// <para>
/// Live ranges may overlap (<see cref="GpuTextureRegistry.StaleInFirstPage"/> keeps a texture whose large levels a newer
/// one covers), but no two live textures start at the same address. Each run keeps the largest end of its entries up to
/// each one, so a search walks back from the last start before an address only while some earlier entry can still reach
/// it: with ranges that do not overlap, that is the one entry it walked back to before.
/// </para>
/// </summary>
internal sealed class TextureRanges
{
    /// <summary>Textures registered since the last rebuild past which the large run is rebuilt.</summary>
    public const int RecentLimit = 256;

    /// <summary>Retired textures left in the large run past which it is rebuilt.</summary>
    public const int DeadLimit = 4096;

    private static readonly GpuTexture[] None = [];

    private Snapshot _snapshot = new(Run.Empty, Run.Empty, 0);
    private int _dead;

    /// <summary>The registered textures.</summary>
    public int Count => Volatile.Read(ref _snapshot).Live;

    /// <summary>The registered textures, by address (under the lock).</summary>
    public IEnumerable<GpuTexture> All
    {
        get
        {
            var snapshot = _snapshot;

            return snapshot.Large.Entries.Where(entry => !entry.Unregistered).Concat(snapshot.Recent.Entries).OrderBy(entry => entry.Address);
        }
    }

    /// <summary>
    /// The registered texture whose range holds <paramref name="address"/>, the one that starts last when several do
    /// (so the texture that starts at the address, when one does); null for none (no lock).
    /// </summary>
    public GpuTexture? Find(ulong address)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var recent = snapshot.Recent.Containing(address);
        var large = snapshot.Large.Containing(address);

        if (recent is null)
        {
            return large;
        }

        if (large is not null && large.Address > recent.Address)
        {
            return large;
        }

        return recent;
    }

    /// <summary>Whether a registered texture overlaps the range (no lock).</summary>
    public bool Overlaps(ulong address, ulong bytes)
    {
        var snapshot = Volatile.Read(ref _snapshot);

        return snapshot.Recent.AnyOverlapping(address, bytes) || snapshot.Large.AnyOverlapping(address, bytes);
    }

    /// <summary>The registered textures over any byte of the range, by address; an empty array for none (no lock).</summary>
    public IReadOnlyList<GpuTexture> Overlapping(ulong address, ulong bytes)
    {
        var snapshot = Volatile.Read(ref _snapshot);

        if (!snapshot.Recent.AnyOverlapping(address, bytes) && !snapshot.Large.AnyOverlapping(address, bytes))
        {
            return None;
        }

        var found = new List<GpuTexture>();
        snapshot.Large.AddOverlapping(address, bytes, found);
        snapshot.Recent.AddOverlapping(address, bytes, found);

        if (found.Count > 1)
        {
            found.Sort((left, right) => left.Address.CompareTo(right.Address));
        }

        return found;
    }

    /// <summary>
    /// Under the lock: <paramref name="texture"/> is registered and <paramref name="replaced"/> (registered textures) are
    /// not. The new texture is visible before the replaced ones go, so a lookup meanwhile finds one or the other.
    /// </summary>
    public void Add(GpuTexture texture, IReadOnlyList<GpuTexture> replaced)
    {
        var snapshot = _snapshot;
        var recent = snapshot.Recent.Entries;

        if (replaced.Count > 0)
        {
            recent = Without(recent, replaced);
        }

        int at = InsertionPoint(recent, texture.Address);
        var next = new GpuTexture[recent.Length + 1];
        Array.Copy(recent, 0, next, 0, at);
        next[at] = texture;
        Array.Copy(recent, at, next, at + 1, recent.Length - at);

        Volatile.Write(ref _snapshot, new Snapshot(snapshot.Large, new Run(next), snapshot.Live + 1 - replaced.Count));
        Unregister(replaced);
        Tidy();
    }

    /// <summary>Under the lock: <paramref name="removed"/>, registered textures, are not any more.</summary>
    public void Remove(IReadOnlyCollection<GpuTexture> removed)
    {
        if (removed.Count == 0)
        {
            return;
        }

        var snapshot = _snapshot;
        var recent = Without(snapshot.Recent.Entries, removed);

        Volatile.Write(ref _snapshot, new Snapshot(snapshot.Large, new Run(recent), snapshot.Live - removed.Count));
        Unregister(removed);
        Tidy();
    }

    /// <summary>Under the lock: nothing is registered.</summary>
    public void Clear()
    {
        Volatile.Write(ref _snapshot, new Snapshot(Run.Empty, Run.Empty, 0));
        _dead = 0;
    }

    /// <summary>The live entries of both runs, merged by address, become the large run (under the lock).</summary>
    internal void Rebuild()
    {
        var snapshot = _snapshot;
        var live = snapshot.Large.Entries.Where(entry => !entry.Unregistered).ToArray();
        var recent = snapshot.Recent.Entries;
        var merged = new GpuTexture[live.Length + recent.Length];
        int fromLarge = 0;
        int fromRecent = 0;

        for (int count = 0; count < merged.Length; count++)
        {
            if (fromRecent >= recent.Length || (fromLarge < live.Length && live[fromLarge].Address < recent[fromRecent].Address))
            {
                merged[count] = live[fromLarge++];
            }
            else
            {
                merged[count] = recent[fromRecent++];
            }
        }

        Volatile.Write(ref _snapshot, new Snapshot(new Run(merged), Run.Empty, merged.Length));
        _dead = 0;
    }

    /// <summary>Rebuilds once enough was registered or retired since the last time.</summary>
    private void Tidy()
    {
        if (_snapshot.Recent.Entries.Length > RecentLimit || _dead > DeadLimit)
        {
            Rebuild();
        }
    }

    /// <summary>Flags the textures as retired; those of the large run stay there until the next rebuild.</summary>
    private void Unregister(IEnumerable<GpuTexture> textures)
    {
        foreach (var texture in textures)
        {
            texture.Unregistered = true;
            _dead++;
        }
    }

    private static GpuTexture[] Without(GpuTexture[] recent, IEnumerable<GpuTexture> removed)
    {
        var set = removed as IReadOnlySet<GpuTexture> ?? removed.ToHashSet();

        if (!recent.Any(set.Contains))
        {
            return recent;
        }

        return recent.Where(entry => !set.Contains(entry)).ToArray();
    }

    /// <summary>The index of the first texture of <paramref name="sorted"/> that starts at or past <paramref name="address"/>.</summary>
    private static int InsertionPoint(GpuTexture[] sorted, ulong address)
    {
        int low = 0;
        int high = sorted.Length;

        while (low < high)
        {
            int middle = low + ((high - low) >> 1);

            if (sorted[middle].Address < address)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>Textures sorted by start, and the largest end of the entries up to each (retired ones included).</summary>
    private sealed class Run
    {
        public static readonly Run Empty = new(None);

        public Run(GpuTexture[] entries)
        {
            Entries = entries;
            Reach = new ulong[entries.Length];
            ulong reach = 0;

            for (int index = 0; index < entries.Length; index++)
            {
                reach = Math.Max(reach, entries[index].Address + entries[index].Size);
                Reach[index] = reach;
            }
        }

        public GpuTexture[] Entries { get; }

        /// <summary>The largest end of the entries up to each index.</summary>
        public ulong[] Reach { get; }

        /// <summary>The live texture holding <paramref name="address"/> that starts last; null for none.</summary>
        public GpuTexture? Containing(ulong address)
        {
            for (int index = InsertionPoint(Entries, address + 1) - 1; index >= 0 && Reach[index] > address; index--)
            {
                var entry = Entries[index];

                if (!entry.Unregistered && entry.Contains(address))
                {
                    return entry;
                }
            }

            return null;
        }

        /// <summary>Whether a live texture overlaps the range.</summary>
        public bool AnyOverlapping(ulong address, ulong bytes)
        {
            for (int index = InsertionPoint(Entries, address + bytes) - 1; index >= 0 && Reach[index] > address; index--)
            {
                var entry = Entries[index];

                if (!entry.Unregistered && address < entry.Address + entry.Size)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Adds the live textures over any byte of the range to <paramref name="found"/>.</summary>
        public void AddOverlapping(ulong address, ulong bytes, List<GpuTexture> found)
        {
            for (int index = InsertionPoint(Entries, address + bytes) - 1; index >= 0 && Reach[index] > address; index--)
            {
                var entry = Entries[index];

                if (!entry.Unregistered && address < entry.Address + entry.Size)
                {
                    found.Add(entry);
                }
            }
        }
    }

    /// <summary>One published state: the large run, the small one of recent registrations, and how many are live.</summary>
    private sealed record Snapshot(Run Large, Run Recent, int Live);
}
