// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Shims.D3D12X.Resources;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// The desktop buffers of <see cref="AddressTable"/> ranges a later range replaced (the game placed a new resource over
/// the same pages, as the console allows while the GPU still uses the old one). Each is released only once no GPU work
/// can reach it: <see cref="RetirePresents"/> presents later the queues' submitted values are taken (lists recorded
/// before the replacement are submitted by then), and it goes once every queue has completed them. Released at once,
/// a buffer lists in flight still wrote through was freed under them (w170: hair and skinning compute, device removed).
/// </summary>
internal sealed class RetiredAddressBuffers
{
    /// <summary>Presents between the replacement and taking the queues' submitted values.</summary>
    public const long RetirePresents = 8;

    private readonly object _gate = new();
    private readonly List<RetiredBuffer> _waiting = [];
    private readonly Func<ulong[]> _submitted;
    private readonly Func<ulong[]> _completed;
    private readonly Action<nint> _release;
    private readonly LoaderLog? _log;
    private long _present;
    private long _retired;
    private long _released;

    /// <param name="submitted">Every queue's submitted fence value now.</param>
    /// <param name="completed">Every queue's completed fence value now.</param>
    /// <param name="release">Drops the table's reference on a buffer.</param>
    /// <param name="log">Where the counts go; none in tests.</param>
    public RetiredAddressBuffers(Func<ulong[]> submitted, Func<ulong[]> completed, Action<nint> release, LoaderLog? log)
    {
        _submitted = submitted;
        _completed = completed;
        _release = release;
        _log = log;
    }

    /// <summary>Buffers retired so far.</summary>
    public long Retired => Interlocked.Read(ref _retired);

    /// <summary>Retired buffers released so far.</summary>
    public long Released => Interlocked.Read(ref _released);

    /// <summary>Retires <paramref name="resource"/>, which the address table no longer names.</summary>
    public void Retire(nint resource)
    {
        if (resource == 0)
        {
            return;
        }

        lock (_gate)
        {
            _waiting.Add(new RetiredBuffer(resource, _present));
        }

        Interlocked.Increment(ref _retired);
    }

    /// <summary>Once per <paramref name="present"/>: takes the submitted values of buffers retired long enough ago, and releases those the queues are past.</summary>
    public void Advance(long present)
    {
        if (Volatile.Read(ref _present) == present)
        {
            return;
        }

        var due = new List<nint>();

        lock (_gate)
        {
            if (_present == present)
            {
                return;
            }

            Volatile.Write(ref _present, present);
            Due(present, due);
        }

        // Released outside the lock: a release runs the driver.
        foreach (nint resource in due)
        {
            _release(resource);
        }

        Report(due.Count);
    }

    /// <summary>Under the lock: moves the buffers whose queues are past them to <paramref name="due"/>.</summary>
    private void Due(long present, List<nint> due)
    {
        ulong[]? submitted = null;
        ulong[]? completed = null;

        for (int i = _waiting.Count - 1; i >= 0; i--)
        {
            var retired = _waiting[i];

            if (retired.Marks is null)
            {
                if (present - retired.Present >= RetirePresents)
                {
                    submitted ??= _submitted();
                    _waiting[i] = retired with { Marks = submitted };
                }

                continue;
            }

            completed ??= _completed();

            if (GpuTextureRegistry.Finished(retired.Marks, completed))
            {
                due.Add(retired.Resource);
                _waiting.RemoveAt(i);
            }
        }
    }

    /// <summary>The first release, and then each power of two, logs the counts.</summary>
    private void Report(int released)
    {
        if (released == 0)
        {
            return;
        }

        long total = Interlocked.Add(ref _released, released);

        if (total == released || BitOperations.Log2((ulong)total) != BitOperations.Log2((ulong)(total - released)))
        {
            _log?.Info($"[d3d12_xs] address table: {total} replaced buffers released once the queues were past them ({Retired} retired); their map pages were pointed away first");
        }
    }

    /// <param name="Resource">The buffer.</param>
    /// <param name="Present">The present it was retired at.</param>
    /// <param name="Marks">The queues' submitted values taken <see cref="RetirePresents"/> later; null before.</param>
    private readonly record struct RetiredBuffer(nint Resource, long Present, ulong[]? Marks = null);
}
