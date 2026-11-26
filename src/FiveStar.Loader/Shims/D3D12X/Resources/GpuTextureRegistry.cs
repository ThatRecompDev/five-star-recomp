// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// The desktop textures standing for console GPU address ranges, sorted by address. Lookups read the current array
/// without a lock; registering replaces it. A new texture retires every texture whose range it overlaps (the memory
/// was given to something else). The newest <see cref="KeptAliases"/> retired textures over exactly one range (other
/// views of the same memory) keep their reference until disposal, as descriptors the game wrote earlier may still name
/// them; older ones are released <see cref="AliasGraceFrames"/> presents later. Any other retired texture is
/// released <see cref="GraceFrames"/> presents later. Either is released only once every queue has completed the work
/// it had submitted when the texture was retired (its shim fences, <see cref="QueueProgress"/>), so no list that could
/// sample it is still on the GPU; the presents give the game time to rewrite its descriptors for the new contents.
/// The grace was 120 and 600 presents, without the fences: boot w36 held 4,905 retired textures, their video memory
/// charged to commit, when commit reached its limit. Before that every streamed-out texture stayed alive: a cutscene's
/// streaming grew the process by about 3 GB a minute once video memory spilled into system memory.
/// </summary>
internal sealed class GpuTextureRegistry : IDisposable
{
    /// <summary>Presents between retiring a texture whose memory was reused and releasing it, its submitted work done.</summary>
    public const long GraceFrames = 8;

    /// <summary>
    /// Retired textures over exactly one range kept until disposal, the newest ones, by default (<c>--kept-aliases</c>). Two
    /// kept a second texture alive per reused streaming slot, its video memory charged to commit through the driver's
    /// write-combined mirror; one keeps the latest the game's older descriptors may still name, and a placed texture's heap range stays mapped
    /// until its texture goes, so a stale read never reaches freed memory (issue #23).
    /// </summary>
    public const int DefaultKeptAliases = 1;

    /// <summary>Retired textures over exactly one range kept until disposal for registries made from now on (<c>--kept-aliases</c>).</summary>
    public static int KeptAliases { get; set; } = DefaultKeptAliases;

    /// <summary>Presents before an older texture over the same range is released, its submitted work done.</summary>
    public const long AliasGraceFrames = 30;

    private const double BytesPerGigabyte = 1UL << 30;

    private readonly object _gate = new();
    private readonly Dictionary<(ulong Address, ulong Size), Queue<GpuTexture>> _aliases = [];
    private readonly Queue<RetiredTexture> _retired = new();
    private readonly Queue<RetiredTexture> _agedAliases = new();
    private readonly HashSet<GpuTexture> _overUnmapped = [];
    private readonly Func<ulong[]> _submitted;
    private readonly Func<ulong[]> _completed;
    private int _keptAliases = KeptAliases;
    private int _aliasCount;
    private GpuTexture[] _sorted = [];
    private long _frame;
    private long _released;
    private long _unmapped;
    private long _unmappedBytes;

    /// <summary>The registry of the loader's queues (<see cref="QueueProgress"/>).</summary>
    public static GpuTextureRegistry OfQueues()
    {
        return OfQueues(KeptAliases);
    }

    /// <summary>A registry over the shim queues' fences keeping <paramref name="kept"/> retired textures per range.</summary>
    public static GpuTextureRegistry OfQueues(int kept)
    {
        return new GpuTextureRegistry(QueueProgress.Submitted, QueueProgress.Completed) { _keptAliases = kept };
    }

    /// <param name="submitted">Every queue's submitted fence value now.</param>
    /// <param name="completed">Every queue's completed fence value now.</param>
    public GpuTextureRegistry(Func<ulong[]> submitted, Func<ulong[]> completed)
    {
        _submitted = submitted;
        _completed = completed;
    }

    /// <summary>Whether every queue has completed what it had <paramref name="submitted"/>.</summary>
    public static bool Finished(ulong[] submitted, ulong[] completed)
    {
        for (int queue = 0; queue < submitted.Length; queue++)
        {
            if (queue >= completed.Length || completed[queue] < submitted[queue])
            {
                return false;
            }
        }

        return true;
    }

    public int Count => Volatile.Read(ref _sorted).Length;

    /// <summary>Retired textures over one range this registry keeps until disposal.</summary>
    public int Kept => _keptAliases;

    /// <summary>Retired textures still holding their reference.</summary>
    public int RetiredCount
    {
        get
        {
            lock (_gate)
            {
                return _aliasCount + _retired.Count + _agedAliases.Count;
            }
        }
    }

    /// <summary>Retired textures released so far.</summary>
    public long Released => Interlocked.Read(ref _released);

    /// <summary>"N textures, retired R (aliased A), released N", for the texture counters.</summary>
    public string Summary
    {
        get
        {
            lock (_gate)
            {
                int retired = _aliasCount + _retired.Count + _agedAliases.Count;

                return $"{_sorted.Length} textures, retired {retired} (aliased {_aliasCount + _agedAliases.Count}, {AliasBytes() / BytesPerGigabyte:F2} GB), released {_released}, {_unmapped} retired as their memory was unmapped, {_overUnmapped.Count} live over unmapped memory ({_unmappedBytes / BytesPerGigabyte:F2} GB)";
            }
        }
    }

    /// <summary>
    /// Adds <paramref name="texture"/> and retires the textures its range overlaps. Registered ranges never overlap, so
    /// those form one run of the sorted array, found by binary search, and the new array is the run's prefix, the
    /// texture and its suffix. It used to copy and sort the whole array on every registration: with 15,000 textures a
    /// loading screen's registrations kept the thread that creates the views busy for seconds a frame, and the game's
    /// 30 s hang watchdog ended the boot.
    /// </summary>
    public void Register(GpuTexture texture)
    {
        lock (_gate)
        {
            var sorted = _sorted;
            var (first, count) = OverlapRun(sorted, texture.Address, texture.Size);

            for (int index = first; index < first + count; index++)
            {
                Retire(sorted[index], texture);
            }

            var next = new GpuTexture[sorted.Length - count + 1];
            Array.Copy(sorted, 0, next, 0, first);
            next[first] = texture;
            Array.Copy(sorted, first + count, next, first + 1, sorted.Length - first - count);
            Volatile.Write(ref _sorted, next);
        }
    }

    /// <summary>
    /// The run of <paramref name="sorted"/> overlapping [<paramref name="address"/>, + <paramref name="bytes"/>): its first
    /// index and length; with no overlap, the index the range belongs at and 0.
    /// </summary>
    internal static (int First, int Count) OverlapRun(GpuTexture[] sorted, ulong address, ulong bytes)
    {
        int last = LastStartingBefore(sorted, address + bytes);
        int first = last + 1;

        while (first > 0 && address < sorted[first - 1].Address + sorted[first - 1].Size)
        {
            first--;
        }

        return (first, last + 1 - first);
    }

    /// <summary>
    /// <c>--retire-unmapped-textures</c>: a texture whose memory stays unmapped for <see cref="UnmappedGraceFrames"/>
    /// presents is retired (released after its grace). Off by default: the game maps pages again at the same address and
    /// may read through descriptors it made before; in w82 the textures over unmapped memory rose to 4,264 (3.4 GB) and
    /// fell again, so most come back. Without it they are only counted (<see cref="Summary"/>).
    /// </summary>
    public static bool RetireUnmapped { get; set; }

    /// <summary>Presents a texture's memory must stay unmapped before <see cref="RetireUnmapped"/> retires it (about 20 s at 30 fps).</summary>
    public const long UnmappedGraceFrames = 600;

    /// <summary>
    /// The game unmapped the memory at [<paramref name="address"/>, + <paramref name="bytes"/>): textures over it are
    /// marked with the present it happened at (counted in <see cref="Summary"/>; retired by <see cref="Advance"/> once it
    /// stays unmapped long enough, with <see cref="RetireUnmapped"/>). Only a new texture over the same range retired one
    /// before, and #13's long run grew its placed texture heaps from 0.5 to 10.5 GB. Returns how many it marked.
    /// </summary>
    public int Unmapped(ulong address, ulong bytes)
    {
        if (!Overlaps(Volatile.Read(ref _sorted), address, bytes))
        {
            return 0;
        }

        lock (_gate)
        {
            return Mark(address, bytes, unmapped: true);
        }
    }

    /// <summary>The game mapped memory at the range again: textures over it are no longer over unmapped memory.</summary>
    public void Mapped(ulong address, ulong bytes)
    {
        if (!Overlaps(Volatile.Read(ref _sorted), address, bytes))
        {
            return;
        }

        lock (_gate)
        {
            Mark(address, bytes, unmapped: false);
        }
    }

    /// <summary>
    /// Marks the textures over the range as over unmapped memory (from now) or not, under the lock; how many changed.
    /// Only the run over the range is visited (<see cref="OverlapRun"/>): each page the game maps or unmaps used to scan
    /// all 15,000 textures, which kept the page-mapping thread busy through a loading screen.
    /// </summary>
    private int Mark(ulong address, ulong bytes, bool unmapped)
    {
        int changed = 0;
        var sorted = _sorted;
        var (first, count) = OverlapRun(sorted, address, bytes);

        for (int index = first; index < first + count; index++)
        {
            var entry = sorted[index];

            if (entry.OverUnmapped != unmapped)
            {
                Unmark(entry);

                if (unmapped)
                {
                    entry.UnmappedSince = _frame;
                    _overUnmapped.Add(entry);
                    _unmappedBytes += (long)entry.Size;
                }

                changed++;
            }
        }

        return changed;
    }

    private void Unmark(GpuTexture entry)
    {
        if (entry.OverUnmapped)
        {
            entry.UnmappedSince = GpuTexture.Mapped;
            _overUnmapped.Remove(entry);
            _unmappedBytes -= (long)entry.Size;
        }
    }

    /// <summary>With <see cref="RetireUnmapped"/>, under the lock: retires the textures unmapped since <paramref name="lastFrame"/> or before.</summary>
    private void RetireLongUnmapped(long lastFrame)
    {
        var due = _overUnmapped.Where(entry => entry.UnmappedSince <= lastFrame).ToHashSet();

        if (due.Count == 0)
        {
            return;
        }

        foreach (var entry in due)
        {
            Unmark(entry);
            _retired.Enqueue(new RetiredTexture(entry, _frame, _submitted()));
        }

        Volatile.Write(ref _sorted, _sorted.Where(entry => !due.Contains(entry)).ToArray());
        _unmapped += due.Count;
    }

    /// <summary>
    /// Whether a texture of the sorted array overlaps the range (lock-free, before the rebuild): registered ranges never
    /// overlap, so only the last texture starting before the range's end can reach into it.
    /// </summary>
    private static bool Overlaps(GpuTexture[] sorted, ulong address, ulong bytes)
    {
        int last = LastStartingBefore(sorted, address + bytes);

        return last >= 0 && address < sorted[last].Address + sorted[last].Size;
    }

    /// <summary>The index of the last texture of <paramref name="sorted"/> that starts before <paramref name="end"/>; -1 for none.</summary>
    private static int LastStartingBefore(GpuTexture[] sorted, ulong end)
    {
        int low = 0;
        int high = sorted.Length - 1;
        int last = -1;

        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);

            if (sorted[middle].Address < end)
            {
                last = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return last;
    }

    /// <summary>The registered textures over any byte of the range (lock-free: registered ranges never overlap).</summary>
    public IReadOnlyList<GpuTexture> Overlapping(ulong address, ulong bytes)
    {
        var sorted = Volatile.Read(ref _sorted);
        int index = LastStartingBefore(sorted, address + bytes);

        if (index < 0 || address >= sorted[index].Address + sorted[index].Size)
        {
            return [];
        }

        var found = new List<GpuTexture>();

        for (; index >= 0 && address < sorted[index].Address + sorted[index].Size; index--)
        {
            found.Add(sorted[index]);
        }

        return found;
    }

    /// <summary>Once per present: releases the retired textures whose grace period ended.</summary>
    public void Advance(long frame)
    {
        var due = new List<GpuTexture>();

        lock (_gate)
        {
            _frame = frame;

            if (RetireUnmapped && _overUnmapped.Count > 0)
            {
                RetireLongUnmapped(frame - UnmappedGraceFrames);
            }

            if (_retired.Count == 0 && _agedAliases.Count == 0)
            {
                return;
            }

            var completed = _completed();
            ReleaseDue(_retired, frame - GraceFrames, completed, due);
            ReleaseDue(_agedAliases, frame - AliasGraceFrames, completed, due);
        }

        // Released outside the lock: a release runs the driver and the placed range's owner (PlacedTextureHeaps).
        var releasing = Releasing;

        foreach (var texture in due)
        {
            releasing?.Invoke(texture);
            ComObject.Release(texture.Desktop);
        }
    }

    /// <summary>The bytes of the retired textures kept over their range (under the lock).</summary>
    private long AliasBytes()
    {
        long bytes = _agedAliases.Sum(entry => (long)entry.Texture.Size);

        foreach (var queue in _aliases.Values)
        {
            bytes += queue.Sum(texture => (long)texture.Size);
        }

        return bytes;
    }

    /// <summary>Called with each texture this registry releases, just before (its owner's accounting).</summary>
    public Action<GpuTexture>? Releasing { get; set; }

    /// <summary>
    /// Takes, oldest first, the textures retired by <paramref name="lastFrame"/> whose work is done. A texture's work is
    /// what the queues had submitted when its grace ended, not only when it was retired: a list recorded before the
    /// retire with a descriptor of the texture may be submitted during the grace, and the first snapshot misses it.
    /// </summary>
    private void ReleaseDue(Queue<RetiredTexture> queue, long lastFrame, ulong[] completed, List<GpuTexture> due)
    {
        while (queue.Count > 0 && queue.Peek().Frame <= lastFrame)
        {
            var head = queue.Peek();

            if (!head.Rearmed)
            {
                head.Submitted = _submitted();
                head.Rearmed = true;
            }

            if (!Finished(head.Submitted, completed))
            {
                return;
            }

            due.Add(queue.Dequeue().Texture);
            _released++;
        }
    }

    /// <summary>The texture whose range holds <paramref name="address"/>, or null.</summary>
    [HotPath]
    public GpuTexture? Find(ulong address)
    {
        var sorted = Volatile.Read(ref _sorted);
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

    public void Dispose()
    {
        lock (_gate)
        {
            var aliases = _aliases.Values.SelectMany(queue => queue);
            var retired = _retired.Concat(_agedAliases).Select(entry => entry.Texture);

            foreach (var entry in _sorted.Concat(aliases).Concat(retired))
            {
                ComObject.Release(entry.Desktop);
            }

            _aliases.Clear();
            _aliasCount = 0;
            _retired.Clear();
            _agedAliases.Clear();
            _sorted = [];
        }
    }

    private void Retire(GpuTexture entry, GpuTexture replacement)
    {
        Unmark(entry);

        if (DebugLayer.TargetClears.Census && entry.GpuWritten && replacement.GpuWritten)
        {
            DebugLayer.TargetClears.Replaced(entry, replacement);
        }

        if (entry.Address == replacement.Address && entry.Size == replacement.Size)
        {
            Alias(entry);

            return;
        }

        _retired.Enqueue(new RetiredTexture(entry, _frame, _submitted()));
    }

    /// <summary>
    /// Keeps a texture retired by another over exactly its range; past <see cref="KeptAliases"/> for that range the
    /// oldest is released <see cref="AliasGraceFrames"/> presents later. All were kept before, and a streaming pool
    /// reuses fixed-size slots: the growth after the story load followed the textures made.
    /// </summary>
    private void Alias(GpuTexture entry)
    {
        var range = (entry.Address, entry.Size);

        if (!_aliases.TryGetValue(range, out var kept))
        {
            kept = new Queue<GpuTexture>();
            _aliases[range] = kept;
        }

        kept.Enqueue(entry);
        _aliasCount++;

        if (kept.Count > _keptAliases)
        {
            _agedAliases.Enqueue(new RetiredTexture(kept.Dequeue(), _frame, _submitted()));
            _aliasCount--;
        }
    }

    /// <summary>A retired texture: the present it was retired at and the queues' submitted work it waits for.</summary>
    private sealed class RetiredTexture(GpuTexture texture, long frame, ulong[] submitted)
    {
        public GpuTexture Texture { get; } = texture;

        public long Frame { get; } = frame;

        public ulong[] Submitted { get; set; } = submitted;

        /// <summary>Whether <see cref="Submitted"/> was taken again when the grace ended.</summary>
        public bool Rearmed { get; set; }
    }
}
