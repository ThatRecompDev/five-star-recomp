// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Native;

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// The threads one <c>--cpu-sample</c> window samples, each with a query handle, the cycles it had run at its last
/// sample and that sample. A thread whose last sample was blocked and whose cycle count has not moved since is still in
/// that wait: its sample is counted again without a snapshot (one system call instead of a suspend, a stack copy and
/// an unwind). In the world nine samples in ten are blocked, and snapshots of them were most of the sampler's cost.
/// </summary>
internal sealed unsafe class CpuThreads : IDisposable
{
    /// <summary>THREAD_QUERY_LIMITED_INFORMATION (winnt.h): enough for QueryThreadCycleTime.</summary>
    private const uint QueryLimited = 0x0800;

    private readonly nint[] _handles;
    private readonly ulong[] _cycles;
    private readonly CpuSample?[] _last;

    public CpuThreads(uint[] ids)
    {
        Ids = ids;
        _handles = [.. ids.Select(id => Kernel32.OpenThread(QueryLimited, 0, id))];
        _cycles = new ulong[ids.Length];
        _last = new CpuSample?[ids.Length];
    }

    public uint[] Ids { get; }

    public int Count => Ids.Length;

    /// <summary>Samples counted again for a blocked thread that had not run since.</summary>
    public int Reused { get; private set; }

    /// <summary>The thread's sample now: the last one again when it is still blocked there, else <paramref name="take"/>'s.</summary>
    public CpuSample Sample(int index, Func<uint, CpuSample> take)
    {
        bool known = Cycles(index, out ulong cycles);
        var last = _last[index];

        if (known && last.HasValue && last.Value.Bucket == CpuBucket.Blocked && cycles == _cycles[index])
        {
            Reused++;

            return last.Value;
        }

        var sample = take(Ids[index]);

        // Read after the snapshot: the suspend runs a kernel APC on the thread, which counts cycles of its own.
        _ = Cycles(index, out _cycles[index]);
        _last[index] = sample;

        return sample;
    }

    private bool Cycles(int index, out ulong cycles)
    {
        ulong value = 0;
        bool known = _handles[index] != 0 && Kernel32.QueryThreadCycleTime(_handles[index], &value) != 0;
        cycles = value;

        return known;
    }

    public void Dispose()
    {
        foreach (nint handle in _handles)
        {
            if (handle != 0)
            {
                _ = Kernel32.CloseHandle(handle);
            }
        }
    }
}
