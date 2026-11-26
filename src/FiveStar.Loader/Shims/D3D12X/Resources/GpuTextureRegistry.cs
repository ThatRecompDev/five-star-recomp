// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// The desktop textures standing for console GPU address ranges, by address (<see cref="TextureRanges"/>). Lookups
/// read without a lock; registering and retiring change the ranges under it. A new texture retires every texture whose range it overlaps (the memory
/// was given to something else). The newest <see cref="KeptAliases"/> retired textures over exactly one range (other
/// views of the same memory) keep their reference until disposal, as descriptors the game wrote earlier may still name
/// them; older ones are released <see cref="AliasGraceFrames"/> presents later. Any other retired texture is
/// released <see cref="GraceFrames"/> presents later. Either is released only once every queue has completed the work
/// it had submitted when the texture was retired (its shim fences, <see cref="QueueProgress"/>), so no list that could
/// sample it is still on the GPU; the presents give the game time to rewrite its descriptors for the new contents.
/// Descriptors still holding its views then are written again (<see cref="Unviewing"/>), and it is released only once
/// the queues complete what they submitted after that. The grace was 120 and 600 presents, without the fences: boot w36 held 4,905 retired textures, their video memory
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

    /// <summary>Textures whose views were written again before release, waiting for the queues' work since (<see cref="Unviewing"/>).</summary>
    private readonly Queue<RetiredTexture> _unviewed = new();
    private readonly HashSet<GpuTexture> _overUnmapped = [];
    private readonly ConcurrentDictionary<nint, long> _lastUse = new();
    private long _keptInUse;
    private readonly Func<ulong[]> _submitted;
    private readonly Func<ulong[]> _completed;
    private int _keptAliases = KeptAliases;
    private int _aliasCount;
    private long _aliasBytes;

    /// <summary>The order of kept textures is rebuilt past this many entries per kept texture (plus a base).</summary>
    private const int OrderSlack = 4;
    private const int OrderSlackBase = 64;

    private readonly Queue<GpuTexture> _aliasOrder = new();
    private readonly TextureRanges _ranges = new();
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

    public int Count => _ranges.Count;

    /// <summary>Retired textures over one range this registry keeps until disposal.</summary>
    public int Kept => _keptAliases;

    /// <summary>Retired textures still holding their reference.</summary>
    public int RetiredCount
    {
        get
        {
            lock (_gate)
            {
                return _aliasCount + _retired.Count + _agedAliases.Count + _unviewed.Count;
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
                int retired = _aliasCount + _retired.Count + _agedAliases.Count + _unviewed.Count;

                return $"{_ranges.Count} textures, retired {retired} (aliased {_aliasCount + _agedAliases.Count}, {AliasBytes() / BytesPerGigabyte:F2} GB; {_unviewed.Count} with their views rewritten, waiting for the GPU), released {Released},{_unmapped} retired as their memory was unmapped, {_overUnmapped.Count} live over unmapped memory ({_unmappedBytes / BytesPerGigabyte:F2} GB)";
            }
        }
    }

    /// <summary>
    /// Adds <paramref name="texture"/> and retires the textures its range overlaps. Registered ranges never overlap, so
    /// those form one run by address, found by binary search (<see cref="TextureRanges"/>, which copies no more than its
    /// recent registrations per call). It used to copy and sort the whole array on every registration: with 15,000
    /// textures a loading screen's registrations kept the thread that creates the views busy for seconds a frame, and
    /// the game's 30 s hang watchdog ended the boot.
    /// </summary>
    public void Register(GpuTexture texture)
    {
        lock (_gate)
        {
            var replaced = _ranges.Overlapping(texture.Address, texture.Size);

            if (OnlyFirstPageStales && replaced.Count > 0)
            {
                replaced = [.. replaced.Where(entry => Stales(texture, entry))];
            }

            foreach (var entry in replaced)
            {
                Retire(entry, texture);
            }

            _ranges.Add(texture, replaced);
        }
    }

    /// <summary>
    /// <c>--stale-in-first-page</c>: a new texture retires an older one it overlaps only when it starts in that one's first
    /// <see cref="StalePageBytes"/> bytes (the older one's small levels). The game keeps only a streamed texture's small
    /// levels, which come first, and places the next texture where the large ones would be (facts from the VIctory fork):
    /// retiring on any overlap made w126 re-create 132,000 textures with the same desc at the same address, each after a
    /// neighbour over its large levels had retired it. Off by default: replaying w126's registrations, it keeps 61,000
    /// textures alive instead of 21,600 (18.8 GB of console bytes instead of 9.8), every one with all its levels.
    /// </summary>
    public static bool StaleInFirstPage { get; set; }

    /// <summary>This registry's <see cref="StaleInFirstPage"/>, as it was when the registry was made.</summary>
    internal bool OnlyFirstPageStales { get; init; } = StaleInFirstPage;

    /// <summary>The bytes at a texture's start a newer texture must start in to retire it under <see cref="StaleInFirstPage"/>.</summary>
    public const ulong StalePageBytes = 4096;

    /// <summary>Whether <paramref name="texture"/> retires <paramref name="older"/>, a registered texture it overlaps (<see cref="StaleInFirstPage"/>).</summary>
    internal static bool Stales(GpuTexture texture, GpuTexture older)
    {
        // Render targets and the like alias their memory as before: any overlap retires.
        if (texture.GpuWritten || older.GpuWritten)
        {
            return true;
        }

        return texture.Address >= older.Address && texture.Address - older.Address < StalePageBytes;
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
        if (!_ranges.Overlaps(address, bytes))
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
        if (!_ranges.Overlaps(address, bytes))
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

        foreach (var entry in _ranges.Overlapping(address, bytes))
        {
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
            entry.UnreferencedSince = GpuTexture.Mapped;
            _overUnmapped.Remove(entry);
            _unmappedBytes -= (long)entry.Size;
        }
    }

    /// <summary>With <see cref="RetireUnmapped"/>, under the lock: retires the textures unmapped since <paramref name="lastFrame"/> or before.</summary>
    private void RetireLongUnmapped(long lastFrame, List<GpuTexture> retiring)
    {
        RetireOverUnmapped(_overUnmapped.Where(entry => entry.UnmappedSince <= lastFrame).ToHashSet(), retiring);
    }

    /// <summary>
    /// With <see cref="RetireUnreferenced"/> and <see cref="Referenced"/>, under the lock: retires the textures over memory
    /// unmapped for <see cref="UnreferencedUnmappedFrames"/> presents that no view record has named for
    /// <see cref="UnreferencedFrames"/> presents (a program cannot read them; their views are nulled first, by
    /// <see cref="Retiring"/>). w111b held 5,985 such textures, 3.7 GB.
    /// </summary>
    private void RetireUnreferencedTextures(long frame, Func<GpuTexture, bool> referenced, List<GpuTexture> retiring)
    {
        var due = new HashSet<GpuTexture>();

        foreach (var entry in _overUnmapped)
        {
            if (referenced(entry))
            {
                entry.UnreferencedSince = GpuTexture.Mapped;

                continue;
            }

            if (entry.UnreferencedSince == GpuTexture.Mapped)
            {
                entry.UnreferencedSince = frame;
            }

            if (frame - entry.UnmappedSince >= UnreferencedUnmappedFrames && frame - entry.UnreferencedSince >= UnreferencedFrames)
            {
                due.Add(entry);
            }
        }

        RetireOverUnmapped(due, retiring);
    }

    /// <summary>Retires <paramref name="due"/>, textures over unmapped memory, under the lock; adds them to <paramref name="retiring"/>.</summary>
    private void RetireOverUnmapped(HashSet<GpuTexture> due, List<GpuTexture> retiring)
    {
        if (due.Count == 0)
        {
            return;
        }

        foreach (var entry in due)
        {
            Unmark(entry);
            _retired.Enqueue(new RetiredTexture(entry, _frame, _submitted()));
            retiring.Add(entry);
        }

        _ranges.Remove(due);
        _unmapped += due.Count;
    }

    /// <summary>
    /// Textures over unmapped memory no view record names are retired (default on; <c>--keep-unmapped-textures</c> turns
    /// it off): see <see cref="RetireUnreferencedTextures"/>. Needs <see cref="Referenced"/>.
    /// </summary>
    public static bool RetireUnreferenced { get; set; } = true;

    /// <summary>Presents a texture's memory must stay unmapped before <see cref="RetireUnreferenced"/> may retire it.</summary>
    public const long UnreferencedUnmappedFrames = 120;

    /// <summary>Presents no view record may name a texture's views before <see cref="RetireUnreferenced"/> retires it.</summary>
    public const long UnreferencedFrames = 60;

    /// <summary>Whether a program can read the texture now (a view record names one of its views); null: none is retired so.</summary>
    public Func<GpuTexture, bool>? Referenced { get; set; }

    /// <summary>Called, outside the lock, with the textures retired over unmapped memory this present: their views go null.</summary>
    public Action<IReadOnlyList<GpuTexture>>? Retiring { get; set; }

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
        return _ranges.Overlapping(address, bytes);
    }

    /// <summary>
    /// Once per present: the retired textures whose grace period ended and whose work is done have their views written
    /// again (<see cref="Unviewing"/>) and are released once the queues complete what they had submitted by then; one
    /// with no views held is released at once.
    /// </summary>
    public void Advance(long frame)
    {
        var due = new List<GpuTexture>();
        var ready = new List<GpuTexture>();
        var retiring = new List<GpuTexture>();
        var referenced = Referenced;

        lock (_gate)
        {
            _frame = frame;

            // Past KeptAliasBytes the oldest kept textures, of any range, age too (w111b kept 2,691, never released), but
            // only those no view record names: a descriptor the game still holds keeps its texture, as before the cap (one
            // released while named gave its heap range to a new texture, whose bytes the old view then read).
            if (_aliasBytes > KeptAliasBytes)
            {
                AgeUnreferenced();
            }

            if (RetireUnmapped && _overUnmapped.Count > 0)
            {
                RetireLongUnmapped(frame - UnmappedGraceFrames, retiring);
            }
            else if (RetireUnreferenced && referenced is not null && _overUnmapped.Count > 0)
            {
                RetireUnreferencedTextures(frame, referenced, retiring);
            }

            if (_retired.Count > 0 || _agedAliases.Count > 0 || _unviewed.Count > 0)
            {
                var completed = _completed();
                TakeUnviewed(completed, ready);
                ReleaseDue(_retired, frame - GraceFrames, completed, due);
                ReleaseDue(_agedAliases, frame - AliasGraceFrames, completed, due);
            }
        }

        if (retiring.Count > 0)
        {
            Retiring?.Invoke(retiring);
        }

        // Released outside the lock: a release runs the driver and the placed range's owner (PlacedTextureHeaps).
        Release(ready);
        Release(Unview(due));
    }

    /// <summary>
    /// Called outside the lock with the textures about to be released: the descriptors still holding their views are
    /// written again (null views, or views of the texture replacing them). Returns those that had views: each waits for
    /// the queues' work submitted after the rewrite (a list that read the old descriptor), the others go at once.
    /// </summary>
    public Func<IReadOnlyList<GpuTexture>, List<GpuTexture>>? Unviewing { get; set; }

    /// <summary>Has <paramref name="due"/>'s views written again; returns those to release now, queues the rest (<see cref="Unviewing"/>).</summary>
    private List<GpuTexture> Unview(List<GpuTexture> due)
    {
        var unviewing = Unviewing;

        if (unviewing is null || due.Count == 0)
        {
            return due;
        }

        var rewritten = unviewing(due).ToHashSet();

        if (rewritten.Count == 0)
        {
            return due;
        }

        var now = due.Where(texture => !rewritten.Contains(texture)).ToList();

        lock (_gate)
        {
            // Taken after the rewrite: any list submitted later reads the new descriptors.
            var submitted = _submitted();

            foreach (var texture in rewritten)
            {
                _unviewed.Enqueue(new RetiredTexture(texture, _frame, submitted));
            }

            // Nothing in flight: no list can read the old descriptors.
            TakeUnviewed(_completed(), now);
        }

        return now;
    }

    /// <summary>Under the lock: takes the textures whose views were written again and whose queues' work since is done.</summary>
    private void TakeUnviewed(ulong[] completed, List<GpuTexture> ready)
    {
        while (_unviewed.Count > 0 && Finished(_unviewed.Peek().Submitted, completed))
        {
            var head = _unviewed.Dequeue();

            // Drawn into since (its depth or target views, Used): kept for another grace, its shader views gone.
            if (_lastUse.TryGetValue(head.Texture.Desktop, out long used) && used > head.Frame)
            {
                _retired.Enqueue(new RetiredTexture(head.Texture, _frame, _submitted()));
                _keptInUse++;

                continue;
            }

            _lastUse.TryRemove(head.Texture.Desktop, out _);
            ready.Add(head.Texture);
        }
    }

    private void Release(List<GpuTexture> textures)
    {
        var releasing = Releasing;

        foreach (var texture in textures)
        {
            releasing?.Invoke(texture);

            // Views of it name a released texture from now on: a capture must not touch it (w161 read a freed one).
            DebugLayer.ViewLiveness.Released(texture.Desktop);
            ComObject.Release(texture.Desktop);
            Interlocked.Increment(ref _released);
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

    /// <summary>Bytes of the textures over unmapped memory now.</summary>
    public long UnmappedBytes
    {
        get
        {
            lock (_gate)
            {
                return _unmappedBytes;
            }
        }
    }

    /// <summary>Bytes of the retired textures kept over their range now.</summary>
    public long KeptBytes
    {
        get
        {
            lock (_gate)
            {
                return AliasBytes();
            }
        }
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
        Rearm(queue, lastFrame);

        while (queue.Count > 0 && queue.Peek().Frame <= lastFrame)
        {
            var head = queue.Peek();

            if (!Finished(head.Submitted, completed))
            {
                return;
            }

            queue.Dequeue();

            // Still drawn into or read (Used, FindReplaced): kept for another grace.
            if (_lastUse.TryGetValue(head.Texture.Desktop, out long used) && used > lastFrame)
            {
                queue.Enqueue(new RetiredTexture(head.Texture, _frame, _submitted()));
                _keptInUse++;

                continue;
            }

            _lastUse.TryRemove(head.Texture.Desktop, out _);
            due.Add(head.Texture);
        }
    }

    /// <summary>
    /// Every texture whose grace ended by <paramref name="lastFrame"/> and that has not yet taken the queues' submitted work
    /// takes it now, all of them the same values. Only the head used to take it, when it came to the front, so behind a
    /// GPU that ran L behind the submissions one texture was released per L: w128's releases stood still for 100 presents
    /// at a time (35,000 retired textures, 31.9 GB of placed heaps) and came all at once when the GPU caught up.
    /// </summary>
    private void Rearm(Queue<RetiredTexture> queue, long lastFrame)
    {
        ulong[]? submitted = null;

        foreach (var entry in queue)
        {
            if (entry.Frame > lastFrame)
            {
                break;
            }

            if (!entry.Rearmed)
            {
                submitted ??= _submitted();
                entry.Submitted = submitted;
                entry.Rearmed = true;
            }
        }
    }

    /// <summary>
    /// A GPU-written texture of <paramref name="description"/>'s extent at <paramref name="address"/> that a newer one
    /// replaced (retired or kept over its range), the newest of them; null for none. On the console both share the
    /// memory and the game keeps drawing into the old one through its depth or target views, so a view of the old
    /// shape reads it. Marks it used, which keeps it from release (<see cref="Used"/>).
    /// </summary>
    public GpuTexture? FindReplaced(ulong address, in ResourceDescription description)
    {
        lock (_gate)
        {
            GpuTexture? found = null;

            foreach (var texture in _agedAliases.Select(entry => entry.Texture).Concat(_aliases.Values.SelectMany(queue => queue)).Concat(_retired.Select(entry => entry.Texture)))
            {
                if (texture.GpuWritten && texture.SameExtent(address, description))
                {
                    found = texture;
                }
            }

            if (found is not null)
            {
                _lastUse[found.Desktop] = _frame;
            }

            return found;
        }
    }

    /// <summary>
    /// A depth or target view of <paramref name="desktop"/> was bound: a retired texture used within
    /// <see cref="GraceFrames"/> presents is not released (the game still draws into it).
    /// </summary>
    [HotPath]
    public void Used(nint desktop)
    {
        _lastUse[desktop] = Volatile.Read(ref _frame);
    }

    /// <summary>Retired textures kept past their grace because the game still used them.</summary>
    public long KeptInUse => Interlocked.Read(ref _keptInUse);

    /// <summary>The texture whose range holds <paramref name="address"/>, or null.</summary>
    [HotPath]
    public GpuTexture? Find(ulong address)
    {
        return _ranges.Find(address);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            var aliases = _aliases.Values.SelectMany(queue => queue);
            var retired = _retired.Concat(_agedAliases).Concat(_unviewed).Select(entry => entry.Texture);

            foreach (var entry in _ranges.All.Concat(aliases).Concat(retired))
            {
                ComObject.Release(entry.Desktop);
            }

            _aliases.Clear();
            _aliasCount = 0;
            _aliasBytes = 0;
            _aliasOrder.Clear();

            _retired.Clear();
            _agedAliases.Clear();
            _unviewed.Clear();
            _ranges.Clear();
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
        _aliasBytes += (long)entry.Size;
        _aliasOrder.Enqueue(entry);

        if (kept.Count > _keptAliases)
        {
            Age(kept.Dequeue());
        }

        // Textures aged by their range stay in the order until met: drop them now and then.
        if (_aliasOrder.Count > (OrderSlack * _aliasCount) + OrderSlackBase)
        {
            var stillKept = new HashSet<GpuTexture>(_aliases.Values.SelectMany(queue => queue));
            var order = _aliasOrder.Where(stillKept.Contains).ToList();
            _aliasOrder.Clear();

            foreach (var texture in order)
            {
                _aliasOrder.Enqueue(texture);
            }
        }
    }

    /// <summary>Ages the oldest kept textures no view record names until the kept bytes are within the cap. Under the lock.</summary>
    private void AgeUnreferenced()
    {
        var referenced = Referenced;
        var keep = new Queue<GpuTexture>();

        while (_aliasBytes > KeptAliasBytes && _aliasOrder.TryDequeue(out var oldest))
        {
            if (!_aliases.TryGetValue((oldest.Address, oldest.Size), out var queue) || !queue.Contains(oldest))
            {
                continue;
            }

            if (referenced is null || referenced(oldest))
            {
                keep.Enqueue(oldest);

                continue;
            }

            _aliases[(oldest.Address, oldest.Size)] = new Queue<GpuTexture>(queue.Where(texture => texture != oldest));
            Age(oldest);
        }

        // The named ones keep their place, oldest first.
        while (_aliasOrder.TryDequeue(out var rest))
        {
            keep.Enqueue(rest);
        }

        while (keep.TryDequeue(out var kept))
        {
            _aliasOrder.Enqueue(kept);
        }
    }

    /// <summary>A kept texture is no longer kept: it is released <see cref="AliasGraceFrames"/> presents from now. Under the lock.</summary>
    private void Age(GpuTexture texture)
    {
        _agedAliases.Enqueue(new RetiredTexture(texture, _frame, _submitted()));
        _aliasCount--;
        _aliasBytes -= (long)texture.Size;
    }

    /// <summary>
    /// The bytes of retired textures kept over their range for descriptors the game wrote earlier, at most; the oldest
    /// go past it (default 512 MiB).
    /// </summary>
    public static long KeptAliasBytes { get; set; } = DefaultKeptAliasBytes;

    /// <summary>The default <see cref="KeptAliasBytes"/>: 512 MiB.</summary>
    public const long DefaultKeptAliasBytes = 512L << 20;

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
