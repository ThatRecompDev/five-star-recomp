// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.Xg;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// Refreshes the textures memory changed under (<see cref="GpuTextures.MarkWritten"/>: a DirectStorage write, a page
/// mapped) page by page and off the submitting thread. A texture's written pages are read and hashed one by one
/// against <see cref="GpuTexture.PageHashes"/>; only the subresources (mips, slices) a changed page lies in are
/// detiled and uploaded, read whole from memory. A background thread takes the textures as they are marked; a
/// submission (<see cref="Settle"/>) takes the ones left and waits for those in progress, so every texture marked
/// before a submission is current for it. w109's whole-texture refresh on the submitting thread cost 19 ms a frame.
/// </summary>
internal sealed unsafe class TextureRefreshWorker : IDisposable
{
    /// <summary>How long the thread sleeps between looks when nothing signals it.</summary>
    private const int IdleMilliseconds = 50;

    /// <summary>A submission's wait for refreshes in progress, between looks at their count.</summary>
    private const int WaitMilliseconds = 1;

    private const int BitsPerWord = 64;

    /// <summary>
    /// Threads a submission refreshes the queued textures on: half the logical processors, at least one. The game's
    /// thread waits for the whole queue there anyway, and reading, hashing and detiling a texture run outside the
    /// uploader's lock; a loading screen queued about 1,300 textures between two summaries, and one thread took up to
    /// 16.8 s a frame refreshing them, past the game's 30 s hang watchdog.
    /// </summary>
    internal static readonly int SettleThreads = Math.Max(1, Environment.ProcessorCount / 2);

    private readonly GpuTextures _textures;
    private readonly GameMemorySource _source;
    private readonly ConcurrentDictionary<GpuTexture, byte> _marked = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly ManualResetEventSlim _idle = new(true);
    private readonly object _startGate = new();
    private Thread? _thread;
    private nint _device;
    private int _busy;
    private volatile bool _stopping;

    public TextureRefreshWorker(GpuTextures textures, GameMemorySource source)
    {
        _textures = textures;
        _source = source;
    }

    /// <summary>Queues <paramref name="texture"/> (its written pages already marked) for a refresh.</summary>
    public void Add(GpuTexture texture)
    {
        if (_marked.TryAdd(texture, 0))
        {
            _textures.Counters.Add(GpuResourceEvent.Written);
            _signal.Set();
        }
    }

    /// <summary>
    /// Before a submission on <paramref name="device"/>: refreshes the queued textures on this thread along with the
    /// worker, and waits until none is in progress. Starts the worker the first time.
    /// </summary>
    public void Settle(nint device)
    {
        Start(device);

        if (!_marked.IsEmpty)
        {
            Parallel.ForEach(_marked.Keys, new ParallelOptions { MaxDegreeOfParallelism = SettleThreads }, texture => Take(device, texture));
        }

        // The event can be set a moment before another refresh begins; the count decides.
        while (Volatile.Read(ref _busy) > 0)
        {
            _idle.Wait(WaitMilliseconds);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _signal.Set();
        _thread?.Join();
        _signal.Dispose();
        _idle.Dispose();
    }

    /// <summary>The content hash of each <see cref="GpuTexture.PageBytes"/> page of the texture's bytes at <paramref name="source"/>.</summary>
    public static ulong[] HashPages(GpuTexture texture, byte* source)
    {
        var hashes = new ulong[texture.Pages];

        for (int page = 0; page < hashes.Length; page++)
        {
            var (offset, length) = PageRange(texture, page);
            hashes[page] = ContentHash.Of(source + offset, length);
        }

        return hashes;
    }

    /// <summary>One hash of all the page hashes: the texture's <see cref="GpuTexture.UploadedHash"/>.</summary>
    public static ulong Combined(ulong[] pages)
    {
        fixed (ulong* hashes = pages)
        {
            return ContentHash.Of((byte*)hashes, (ulong)pages.Length * sizeof(ulong));
        }
    }

    /// <summary>
    /// The subresources of <paramref name="surface"/> (<paramref name="count"/> of them) a changed page of
    /// <paramref name="changed"/> lies in, and in <paramref name="needed"/> the pages those span (to read whole).
    /// </summary>
    public static bool[] Affected(in XgSurface surface, int count, bool[] changed, bool[] needed)
    {
        var affected = new bool[count];

        for (int subresource = 0; subresource < count; subresource++)
        {
            var (start, end) = XgSubresourceSpans.Of(surface, subresource);

            if (end <= start)
            {
                continue;
            }

            int first = (int)(start / GpuTexture.PageBytes);
            int last = (int)((end - 1) / GpuTexture.PageBytes);

            for (int page = first; page <= last && !affected[subresource]; page++)
            {
                affected[subresource] = changed[page];
            }

            if (affected[subresource])
            {
                Array.Fill(needed, true, first, last - first + 1);
            }
        }

        return affected;
    }

    private void Start(nint device)
    {
        if (_thread is not null)
        {
            return;
        }

        lock (_startGate)
        {
            if (_thread is not null)
            {
                return;
            }

            Volatile.Write(ref _device, device);
            _thread = new Thread(Run) { IsBackground = true, Name = "fivestar texture refresh" };
            _thread.Start();
        }
    }

    private void Run()
    {
        while (!_stopping)
        {
            _signal.WaitOne(IdleMilliseconds);

            if (!_marked.IsEmpty)
            {
                Drain(Volatile.Read(ref _device));
            }
        }
    }

    /// <summary>Refreshes queued textures until none is left. A texture counts as in progress before it leaves the queue.</summary>
    private void Drain(nint device)
    {
        foreach (var texture in _marked.Keys)
        {
            Take(device, texture);
        }
    }

    /// <summary>Refreshes <paramref name="texture"/> if it is still queued; it counts as in progress before it leaves the queue.</summary>
    private void Take(nint device, GpuTexture texture)
    {
        Begin();

        try
        {
            if (_marked.TryRemove(texture, out _))
            {
                Refresh(device, texture);
            }
        }
        finally
        {
            End();
        }
    }

    private void Begin()
    {
        if (Interlocked.Increment(ref _busy) == 1)
        {
            _idle.Reset();
        }
    }

    private void End()
    {
        if (Interlocked.Decrement(ref _busy) == 0)
        {
            _idle.Set();
        }
    }

    private void Refresh(nint device, GpuTexture texture)
    {
        if (_textures.Registry.Find(texture.Address) != texture || texture.GpuWritten || texture.CopiedInto || texture.Layout.PlaneCount != 1)
        {
            texture.TakeDirty();

            return;
        }

        _textures.Counters.Add(GpuResourceEvent.WrittenRefreshed);

        lock (texture.RefreshGate)
        {
            var dirty = texture.TakeDirty();

            if (dirty is null)
            {
                return;
            }

            if (texture.PageHashes is not null && texture.UploadedHash != 0)
            {
                RefreshPages(device, texture, dirty);

                return;
            }
        }

        // Never filled yet: the whole texture (which takes the gate itself).
        _textures.RefreshWhole(device, texture);
    }

    /// <summary>Reads the written pages, and uploads the subresources a changed one lies in.</summary>
    private void RefreshPages(nint device, GpuTexture texture, ulong[] dirty)
    {
        var hashes = texture.PageHashes!;
        byte* buffer = GpuTextures.CopyBuffer(texture.Size);
        var changed = new bool[hashes.Length];
        var read = new bool[hashes.Length];
        var fresh = (ulong[])hashes.Clone();
        bool any = false;

        for (int page = 0; page < hashes.Length; page++)
        {
            if ((dirty[page / BitsPerWord] & (1UL << (page % BitsPerWord))) == 0)
            {
                continue;
            }

            ReadPage(texture, page, buffer);
            read[page] = true;
            var (offset, length) = PageRange(texture, page);
            fresh[page] = ContentHash.Of(buffer + offset, length);
            changed[page] = fresh[page] != hashes[page];
            any |= changed[page];
        }

        if (!any)
        {
            _textures.Counters.Add(GpuResourceEvent.UploadSkipped);

            return;
        }

        var layout = texture.Layout;
        var plane = layout.Planes[0];
        var needed = new bool[hashes.Length];
        var affected = Affected(layout, plane.MipCount * plane.Slices, changed, needed);

        for (int page = 0; page < needed.Length; page++)
        {
            if (needed[page] && !read[page])
            {
                ReadPage(texture, page, buffer);
            }
        }

        Upload(device, texture, buffer, affected, fresh, changed);
    }

    private void Upload(nint device, GpuTexture texture, byte* buffer, bool[] affected, ulong[] fresh, bool[] changed)
    {
        if (_textures.Uploader.Upload(device, texture, buffer, affected))
        {
            texture.PageHashes = fresh;
            texture.UploadedHash = Combined(fresh);
            _textures.Counters.Add(GpuResourceEvent.UploadPartial);

            return;
        }

        // Not uploaded: the changed pages stay written, for the next refresh to try again.
        for (int page = 0; page < changed.Length; page++)
        {
            if (changed[page])
            {
                var (offset, length) = PageRange(texture, page);
                texture.MarkPages(offset, offset + length);
            }
        }

        _textures.ReportFailure(texture);
    }

    /// <summary>Copies page <paramref name="page"/> of the texture's memory into its place in <paramref name="buffer"/> (zeros where nothing is readable).</summary>
    private void ReadPage(GpuTexture texture, int page, byte* buffer)
    {
        var (offset, length) = PageRange(texture, page);
        ulong address = texture.Address + offset;

        if (_source.InReservation(address))
        {
            _source.Gather(address, length, buffer + offset);

            return;
        }

        byte* memory = _source.Find(address, length);
        nuint done = 0;

        if (memory == null || Kernel32.ReadProcessMemory(Kernel32.GetCurrentProcess(), (nint)memory, buffer + offset, (nuint)length, &done) == 0 || done != (nuint)length)
        {
            new Span<byte>(buffer + offset, (int)length).Clear();
        }
    }

    private static (ulong Offset, ulong Length) PageRange(GpuTexture texture, int page)
    {
        ulong offset = (ulong)page * GpuTexture.PageBytes;

        return (offset, Math.Min(GpuTexture.PageBytes, texture.Size - offset));
    }
}
