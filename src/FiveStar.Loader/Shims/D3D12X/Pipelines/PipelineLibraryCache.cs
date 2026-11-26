// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// The disk cache of built desktop pipelines (docs/d3d12x-pipelines.md, Disk cache): ID3D12PipelineLibrary1
/// segments in one folder, each pipeline named by <see cref="DesktopStream.Key"/>. Finished segments are mapped and
/// only read; new pipelines go to this process's active library, which is sealed and written to a segment file of its
/// own at most <see cref="FlushDelay"/> after a pipeline is stored, after <see cref="SaveBatch"/> new pipelines and at
/// disposal, so a boot that is killed or crashes loses at most a few seconds of driver builds (it lost up to a minute's,
/// thousands at a boot's start, which the next boot built again while the game waited on a black screen).
/// Segments the driver refuses (another adapter or driver version) are deleted; past <see cref="MaximumBytes"/> the
/// oldest segments are deleted when the cache opens.
/// </summary>
internal sealed unsafe class PipelineLibraryCache : IDisposable
{
    /// <summary>The cache's folder under the loader's work folder when no other is given.</summary>
    public const string FolderName = "pipeline-cache";

    /// <summary>Pipelines stored since the last write that make the saver write at once, keeping the active library small.</summary>
    private const int SaveBatch = 256;
    private const long MaximumBytes = 32L << 30;
    private const double BytesPerMegabyte = 1 << 20;
    /// <summary>
    /// Part of the salt: raised whenever <see cref="DesktopStream.Key"/> or the streams change, so segments of the old
    /// form are deleted instead of answering under keys that now mean something else (2: mesh topology).
    /// </summary>
    private const string Format = "fivestar pipeline cache 2";
    private const string IdFormat = "yyyyMMdd-HHmmss-fff";

    /// <summary>The most a stored pipeline waits for its segment file to be written.</summary>
    private static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(5);

    /// <summary>How often the saver looks for segments other processes wrote (<see cref="Refresh"/>).</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

    /// <summary>Segment files named in this process, over every cache it opens: two never share a name.</summary>
    private static int _sequence;

    private readonly object _active = new();
    private readonly object _saving = new();
    private readonly ConcurrentDictionary<string, PipelineLibrarySegment> _index = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _refusedKeys = new(StringComparer.Ordinal);
    private readonly List<PipelineLibrarySegment> _segments = [];
    private readonly List<string> _activeKeys = [];
    private readonly HashSet<string> _activeSet = new(StringComparer.Ordinal);

    /// <summary>Segment files this process mapped, named for its own writes or passed over; <see cref="Refresh"/> takes the others.</summary>
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly ManualResetEventSlim _stop = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly string _folder;
    private readonly nint _device1;
    private readonly byte[] _salt;
    private readonly LoaderLog _log;
    private Thread? _saver;
    private Thread? _opener;
    private nint _library;
    private string _activePath = string.Empty;
    private bool _dirty;
    private int _unsaved;
    private int _hits;
    private int _stored;
    private int _refused;
    private int _joined;

    private PipelineLibraryCache(string folder, nint device1, byte[] salt, LoaderLog log)
    {
        _folder = folder;
        _device1 = device1;
        _salt = salt;
        _log = log;
    }

    /// <summary>Pipelines loaded from the cache, stored in it, and loads the driver refused.</summary>
    public string Summary => $"pipeline cache: {Volatile.Read(ref _hits)} loaded (cache hits), {Volatile.Read(ref _stored)} stored (built by the driver), {Volatile.Read(ref _refused)} refused, {_index.Count + _activeSet.Count} pipelines in {_folder}";

    /// <summary>
    /// The cache in <paramref name="folder"/> for the device (an ID3D12Device1 or later), or null when the device has
    /// no pipeline libraries or the root signature cannot be serialized.
    /// </summary>
    public static PipelineLibraryCache? Open(string folder, nint device1, LoaderLog log)
    {
        var root = BindlessRoot.Serialized();

        if (root is null)
        {
            return null;
        }

        Directory.CreateDirectory(folder);
        var salt = SHA256.HashData([.. Encoding.ASCII.GetBytes(Format), .. root]);
        var cache = new PipelineLibraryCache(folder, device1, salt, log);

        if (!cache.NewActive())
        {
            log.Warn("[d3d12_xs] pipeline cache off: the device creates no pipeline library");

            return null;
        }

        cache.Load();

        return cache;
    }

    /// <summary>Waits up to <paramref name="timeout"/> until every segment found at opening can be loaded from.</summary>
    public bool WaitForSegments(TimeSpan timeout)
    {
        return _opener is null || _opener.Join(timeout);
    }

    /// <summary>True when the pipeline named <paramref name="key"/> is in the cache.</summary>
    public bool Contains(string key)
    {
        if (_index.ContainsKey(key))
        {
            return true;
        }

        lock (_active)
        {
            return _activeSet.Contains(key);
        }
    }

    /// <summary>
    /// The cached pipeline of <paramref name="stream"/> (S_OK), or a failure when it is not cached. A key the driver
    /// refused once is not offered to it again in this process.
    /// </summary>
    public int Load(DesktopStream stream, Guid* iid, nint* result)
    {
        var key = stream.Key;
        int hr = HResult.NotFound;
        bool known = false;

        if (_refusedKeys.ContainsKey(key))
        {
            return hr;
        }

        if (TryLoadActive(stream, iid, result, out int activeHr))
        {
            known = true;
            hr = activeHr;
        }

        if (!known && _index.TryGetValue(key, out var segment))
        {
            known = true;
            hr = segment.Load(key, stream.Describe(), iid, result);
        }

        // Both loads read the programs' native bytes through the stream's desc.
        GC.KeepAlive(stream);

        if (known && hr >= 0 && stream.Launches)
        {
            MeshLaunch.Mark(*result);
        }

        if (!HResult.Failed(hr))
        {
            Interlocked.Increment(ref _hits);
        }
        else if (known && hr != HResult.Pending && _refusedKeys.TryAdd(key, 0) && Interlocked.Increment(ref _refused) == 1)
        {
            _log.Warn($"[d3d12_xs] pipeline cache: the driver refused a cached pipeline ({HResult.Format(hr)}); refusals are built anew and counted");
        }

        return hr;
    }

    /// <summary>
    /// Loads <paramref name="stream"/> from the active library when it holds it. The library is free-threaded: the load
    /// runs outside the gate, holding a reference, so one the driver recompiles does not hold up the game's other
    /// creations or the stores (as <see cref="PipelineLibrarySegment.Load"/>).
    /// </summary>
    private bool TryLoadActive(DesktopStream stream, Guid* iid, nint* result, out int hr)
    {
        nint active = 0;
        hr = HResult.NotFound;

        lock (_active)
        {
            if (_activeSet.Contains(stream.Key) && _library != 0)
            {
                active = _library;
                DesktopDevice.AddRef(active);
            }
        }

        if (active == 0)
        {
            return false;
        }

        try
        {
            hr = DesktopPipelineLibrary.Load(active, stream.Key, stream.Describe(), iid, result);
        }
        finally
        {
            DesktopDevice.Release(active);
        }

        return true;
    }

    /// <summary>Adds a pipeline built for the stream named <paramref name="key"/> to the active library.</summary>
    public void Store(string key, nint pipeline)
    {
        lock (_active)
        {
            if (_library == 0 || _activeSet.Contains(key) || HResult.Failed(DesktopPipelineLibrary.Store(_library, key, pipeline)))
            {
                return;
            }

            _activeKeys.Add(key);
            _activeSet.Add(key);
            _dirty = true;

            if (++_unsaved >= SaveBatch)
            {
                _wake.Set();
            }
        }

        Interlocked.Increment(ref _stored);
    }

    /// <summary>
    /// Writes the active library to its segment file within <see cref="FlushDelay"/> of a stored pipeline, and after
    /// every <see cref="SaveBatch"/> stored pipelines, until disposal; every <see cref="RefreshInterval"/> it also takes
    /// the segments other processes wrote meanwhile (<see cref="Refresh"/>).
    /// </summary>
    public void StartSaving()
    {
        _saver = new Thread(SaveLoop) { IsBackground = true, Name = "fivestar pipeline cache" };
        _saver.Start();
    }

    /// <summary>
    /// Seals the active library when pipelines were stored since the last write: a new empty library takes its place at
    /// once, the sealed one is written to its segment file outside the lock, then replaced by the mapped file.
    /// </summary>
    public void Save()
    {
        lock (_saving)
        {
            PipelineLibrarySegment sealedSegment;

            lock (_active)
            {
                if (!_dirty || _library == 0)
                {
                    return;
                }

                sealedSegment = PipelineLibrarySegment.Sealed(_activePath, _library, [.. _activeKeys]);
                Add(sealedSegment);

                _activeKeys.Clear();
                _activeSet.Clear();
                _dirty = false;
                _unsaved = 0;
                _library = 0;
                NewActive();
            }

            Persist(sealedSegment);
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _saver?.Join();
        _opener?.Join();
        Save();
        _log.Info($"  [d3d12_xs] {Summary}");

        lock (_active)
        {
            ComObject.Release(_library);
            _library = 0;
        }

        lock (_segments)
        {
            foreach (var segment in _segments)
            {
                segment.Dispose();
            }
        }

        _stop.Dispose();
        _wake.Dispose();
    }

    private void SaveLoop()
    {
        WaitHandle[] wakers = [_stop.WaitHandle, _wake];
        var refreshed = Stopwatch.StartNew();

        // Saves only when something was stored (Save returns at once otherwise), so an idle boot writes nothing.
        while (WaitHandle.WaitAny(wakers, FlushDelay) != 0)
        {
            Save();

            if (refreshed.Elapsed >= RefreshInterval)
            {
                Refresh();
                refreshed.Restart();
            }
        }
    }

    /// <summary>
    /// Maps the segments other processes wrote since this one opened the folder: a warm-up run beside the game (or the
    /// game beside a warm-up). A pipeline one of them built is then loaded here, also by a build still queued, instead
    /// of built again. A segment this process cannot use is passed over, not deleted: its writer may still use it.
    /// </summary>
    public void Refresh()
    {
        List<string> files;

        try
        {
            files = Directory.EnumerateFiles(_folder, "*" + PipelineLibraryFile.Extension).Order(StringComparer.Ordinal).ToList();
        }
        catch (IOException)
        {
            return;
        }

        int joined = 0;

        foreach (var file in files)
        {
            if (_stop.IsSet)
            {
                break;
            }

            if (!Unseen(file))
            {
                continue;
            }

            var segment = MapOther(file);

            if (segment is null)
            {
                continue;
            }

            if (HResult.Failed(segment.Activate(_device1)))
            {
                segment.Dispose();

                continue;
            }

            Add(segment);
            joined += segment.Keys.Count;
        }

        if (joined > 0)
        {
            Interlocked.Add(ref _joined, joined);
            _log.Detail($"  [d3d12_xs] pipeline cache: {joined} pipelines from segments other processes wrote ({Volatile.Read(ref _joined)} since opening)");
        }
    }

    /// <summary>Whether <paramref name="file"/> is new to this process; it is not afterwards.</summary>
    private bool Unseen(string file)
    {
        lock (_files)
        {
            return _files.Add(file);
        }
    }

    /// <summary>Another process's segment file, mapped; null when it is foreign, damaged or gone (deleted by its writer's eviction).</summary>
    private PipelineLibrarySegment? MapOther(string file)
    {
        try
        {
            return PipelineLibrarySegment.Map(file, _salt);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps every segment and indexes its names, oldest first so newer ones name a pipeline last, and deletes foreign
    /// files; the driver's libraries are made on a background thread (<see cref="Activate"/>), since that takes seconds
    /// per segment and the device is being created meanwhile.
    /// </summary>
    private void Load()
    {
        var files = Directory.EnumerateFiles(_folder, "*" + PipelineLibraryFile.Extension).Order(StringComparer.Ordinal).ToList();
        var mapped = new List<PipelineLibrarySegment>();
        files.ForEach(file => Unseen(file));

        foreach (var file in Evict(files))
        {
            var segment = PipelineLibrarySegment.Map(file, _salt);

            if (segment is null)
            {
                _log.Warn($"[d3d12_xs] pipeline cache: {Path.GetFileName(file)} deleted ({Refusal(HResult.Ok)})");
                TryDelete(file);

                continue;
            }

            Add(segment);
            mapped.Add(segment);
        }

        _log.Detail($"  [d3d12_xs] pipeline cache: {_index.Count} pipelines in {mapped.Count} segment(s) of {_folder}");
        _opener = new Thread(() => Activate(mapped)) { IsBackground = true, Name = "fivestar pipeline cache open" };
        _opener.Start();
    }

    /// <summary>Makes the libraries of the mapped segments, newest first; deletes those the driver refuses.</summary>
    private void Activate(List<PipelineLibrarySegment> segments)
    {
        var clock = Stopwatch.StartNew();
        var discarded = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var index = segments.Count - 1; index >= 0 && !_stop.IsSet; index--)
        {
            var segment = segments[index];
            var hr = segment.Activate(_device1);

            if (HResult.Failed(hr))
            {
                var reason = Refusal(hr);
                discarded[reason] = discarded.GetValueOrDefault(reason) + 1;
                Remove(segment);
                TryDelete(segment.Path);
            }
        }

        foreach (var (reason, count) in discarded)
        {
            _log.Warn($"[d3d12_xs] pipeline cache: {count} segment(s) discarded ({reason})");
        }

        _log.Detail($"  [d3d12_xs] pipeline cache: {segments.Count - discarded.Values.Sum()} segment(s) opened in {clock.Elapsed.TotalSeconds:F1} s");
    }

    /// <summary>Takes a segment out of the index (where no newer one names its pipelines) and disposes it.</summary>
    private void Remove(PipelineLibrarySegment segment)
    {
        foreach (var key in segment.Keys)
        {
            _index.TryRemove(new KeyValuePair<string, PipelineLibrarySegment>(key, segment));
        }

        lock (_segments)
        {
            _segments.Remove(segment);
        }

        segment.Dispose();
    }

    private void Add(PipelineLibrarySegment segment)
    {
        lock (_segments)
        {
            _segments.Add(segment);
        }

        foreach (var key in segment.Keys)
        {
            _index[key] = segment;
        }
    }

    /// <summary>Writes a sealed library and puts its mapped file in its place; on failure it stays for this run.</summary>
    private void Persist(PipelineLibrarySegment sealedSegment)
    {
        long size;

        try
        {
            size = sealedSegment.Write(_salt);
        }
        catch (IOException exception)
        {
            _log.Warn($"[d3d12_xs] pipeline cache not written to {sealedSegment.Path}: {exception.Message}");

            return;
        }

        var segment = size < 0 ? null : PipelineLibrarySegment.Map(sealedSegment.Path, _salt);

        if (segment is null || HResult.Failed(segment.Activate(_device1)))
        {
            _log.Warn($"[d3d12_xs] pipeline cache segment {sealedSegment.Path} not written or not reopened ({HResult.Format((int)Math.Min(size, 0))})");

            return;
        }

        Add(segment);

        lock (_segments)
        {
            _segments.Remove(sealedSegment);
        }

        sealedSegment.Dispose();
    }

    private bool NewActive()
    {
        nint library = 0;

        if (HResult.Failed(DesktopDevice.CreatePipelineLibrary(_device1, null, 0, &library)))
        {
            return false;
        }

        _library = library;
        var id = DateTime.UtcNow.ToString(IdFormat, CultureInfo.InvariantCulture);
        _activePath = Path.Combine(_folder, $"{id}-{Environment.ProcessId}-{Interlocked.Increment(ref _sequence)}{PipelineLibraryFile.Extension}");
        Unseen(_activePath);

        return true;
    }

    /// <summary>Deletes the oldest segments while the folder holds more than <see cref="MaximumBytes"/>; returns the rest.</summary>
    private List<string> Evict(List<string> files)
    {
        var total = files.Sum(file => new FileInfo(file).Length);
        var kept = new List<string>(files);

        while (total > MaximumBytes && kept.Count > 0)
        {
            total -= new FileInfo(kept[0]).Length;
            TryDelete(kept[0]);
            kept.RemoveAt(0);
        }

        if (kept.Count < files.Count)
        {
            _log.Warn($"[d3d12_xs] pipeline cache over {MaximumBytes / BytesPerMegabyte:F0} MB: {files.Count - kept.Count} oldest segment(s) deleted");
        }

        return kept;
    }

    private static string Refusal(int hr)
    {
        switch (hr)
        {
            case HResult.Ok:
                return "not a segment of this build";
            case DesktopPipelineLibrary.AdapterNotFound:
                return "made on another adapter";
            case DesktopPipelineLibrary.DriverVersionMismatch:
                return "made by another driver version";
            default:
                return $"refused by the driver: {HResult.Format(hr)}";
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
            // Another process has it open; a later run deletes it.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }
}
