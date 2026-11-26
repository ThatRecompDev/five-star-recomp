// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// The registered textures by address, for <see cref="GpuTextureRegistry"/>: lookups read without a lock, changes are
/// made under the registry's lock. Two sorted arrays: a large one rebuilt now and then and a small one of the textures
/// registered since. A registration copies only the small one; a retired texture of the large one is flagged
/// (<see cref="GpuTexture.Unregistered"/>) and dropped at the next rebuild. Registered ranges never overlap, and neither
/// do the ranges within one array; a live texture of the small one may overlap only retired ones of the large one, so a
/// lookup asks the small one first. #56's registration copied the whole array (prefix, texture, suffix) each time: with
/// about 20,000 textures w126 allocated 1.4 GB of arrays in 20 s that way, 81% of the managed allocations.
/// </summary>
internal sealed class TextureRanges
{
    /// <summary>Textures registered since the last rebuild past which the large array is rebuilt.</summary>
    public const int RecentLimit = 256;

    /// <summary>Retired textures left in the large array past which it is rebuilt.</summary>
    public const int DeadLimit = 4096;

    private static readonly GpuTexture[] None = [];

    private Snapshot _snapshot = new(None, None, 0);
    private int _dead;

    /// <summary>The registered textures.</summary>
    public int Count => Volatile.Read(ref _snapshot).Live;

    /// <summary>The registered textures, by address (under the lock).</summary>
    public IEnumerable<GpuTexture> All
    {
        get
        {
            var snapshot = _snapshot;

            return snapshot.Large.Where(entry => !entry.Unregistered).Concat(snapshot.Recent).OrderBy(entry => entry.Address);
        }
    }

    /// <summary>The registered texture whose range holds <paramref name="address"/>, or null (no lock).</summary>
    public GpuTexture? Find(ulong address)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var recent = Containing(snapshot.Recent, address);

        if (recent is not null)
        {
            return recent;
        }

        var large = Containing(snapshot.Large, address);

        if (large is null || large.Unregistered)
        {
            return null;
        }

        return large;
    }

    /// <summary>Whether a registered texture overlaps the range (no lock).</summary>
    public bool Overlaps(ulong address, ulong bytes)
    {
        var snapshot = Volatile.Read(ref _snapshot);

        return AnyLive(snapshot.Recent, address, bytes) || AnyLive(snapshot.Large, address, bytes);
    }

    /// <summary>The registered textures over any byte of the range, by address; an empty array for none (no lock).</summary>
    public IReadOnlyList<GpuTexture> Overlapping(ulong address, ulong bytes)
    {
        var snapshot = Volatile.Read(ref _snapshot);

        if (!AnyLive(snapshot.Recent, address, bytes) && !AnyLive(snapshot.Large, address, bytes))
        {
            return None;
        }

        var found = new List<GpuTexture>();
        AddLive(snapshot.Large, address, bytes, found);
        AddLive(snapshot.Recent, address, bytes, found);

        if (found.Count > 1)
        {
            found.Sort((left, right) => left.Address.CompareTo(right.Address));
        }

        return found;
    }

    /// <summary>
    /// Under the lock: <paramref name="texture"/> is registered and <paramref name="replaced"/> (the registered textures
    /// its range overlaps) are not. The new texture is visible before the replaced ones go, so a lookup meanwhile finds
    /// one or the other.
    /// </summary>
    public void Add(GpuTexture texture, IReadOnlyList<GpuTexture> replaced)
    {
        var snapshot = _snapshot;
        var recent = snapshot.Recent;

        if (replaced.Count > 0)
        {
            recent = Without(recent, replaced);
        }

        int at = InsertionPoint(recent, texture.Address);
        var next = new GpuTexture[recent.Length + 1];
        Array.Copy(recent, 0, next, 0, at);
        next[at] = texture;
        Array.Copy(recent, at, next, at + 1, recent.Length - at);

        Volatile.Write(ref _snapshot, new Snapshot(snapshot.Large, next, snapshot.Live + 1 - replaced.Count));
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
        var recent = Without(snapshot.Recent, removed);

        Volatile.Write(ref _snapshot, new Snapshot(snapshot.Large, recent, snapshot.Live - removed.Count));
        Unregister(removed);
        Tidy();
    }

    /// <summary>Under the lock: nothing is registered.</summary>
    public void Clear()
    {
        Volatile.Write(ref _snapshot, new Snapshot(None, None, 0));
        _dead = 0;
    }

    /// <summary>The live entries of both arrays, merged by address, become the large array (under the lock).</summary>
    internal void Rebuild()
    {
        var snapshot = _snapshot;
        var live = snapshot.Large.Where(entry => !entry.Unregistered).ToArray();
        var recent = snapshot.Recent;
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

        Volatile.Write(ref _snapshot, new Snapshot(merged, None, merged.Length));
        _dead = 0;
    }

    /// <summary>Rebuilds once enough was registered or retired since the last time.</summary>
    private void Tidy()
    {
        if (_snapshot.Recent.Length > RecentLimit || _dead > DeadLimit)
        {
            Rebuild();
        }
    }

    /// <summary>Flags the textures as retired; those of the large array stay there until the next rebuild.</summary>
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

    /// <summary>The texture of <paramref name="sorted"/> whose range holds <paramref name="address"/>, or null.</summary>
    private static GpuTexture? Containing(GpuTexture[] sorted, ulong address)
    {
        int low = 0;
        int high = sorted.Length - 1;

        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            var entry = sorted[middle];

            if (entry.Contains(address))
            {
                return entry;
            }

            if (address < entry.Address)
            {
                high = middle - 1;
            }
            else
            {
                low = middle + 1;
            }
        }

        return null;
    }

    /// <summary>Whether a live texture of <paramref name="sorted"/> (ranges not overlapping) overlaps the range.</summary>
    private static bool AnyLive(GpuTexture[] sorted, ulong address, ulong bytes)
    {
        for (int index = LastStartingBefore(sorted, address + bytes); index >= 0 && address < sorted[index].Address + sorted[index].Size; index--)
        {
            if (!sorted[index].Unregistered)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Adds the live textures of <paramref name="sorted"/> over any byte of the range to <paramref name="found"/>.</summary>
    private static void AddLive(GpuTexture[] sorted, ulong address, ulong bytes, List<GpuTexture> found)
    {
        for (int index = LastStartingBefore(sorted, address + bytes); index >= 0 && address < sorted[index].Address + sorted[index].Size; index--)
        {
            if (!sorted[index].Unregistered)
            {
                found.Add(sorted[index]);
            }
        }
    }

    /// <summary>The index of the last texture of <paramref name="sorted"/> that starts before <paramref name="end"/>; -1 for none.</summary>
    private static int LastStartingBefore(GpuTexture[] sorted, ulong end)
    {
        return InsertionPoint(sorted, end) - 1;
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

    /// <summary>One published state: the large array, the small one of recent registrations, and how many are live.</summary>
    private sealed record Snapshot(GpuTexture[] Large, GpuTexture[] Recent, int Live);
}
