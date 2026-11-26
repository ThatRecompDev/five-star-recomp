// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Cli.Profile;

/// <summary>The samples of one thread class: counts, threads and methods (<see cref="ManagedProfileSummary"/>).</summary>
internal sealed class ProfileClass
{
    public ProfileClass(string name)
    {
        Name = name;
    }

    public string Name { get; }

    /// <summary>Samples of threads running managed code.</summary>
    public int Managed { get; private set; }

    /// <summary>Samples of threads outside managed code: in native code, or waiting.</summary>
    public int External { get; private set; }

    public HashSet<int> Threads { get; } = [];

    /// <summary>Managed samples by innermost method.</summary>
    public Dictionary<string, int> Exclusive { get; } = new(StringComparer.Ordinal);

    /// <summary>Managed samples by every method on the stack, once per sample.</summary>
    public Dictionary<string, int> Inclusive { get; } = new(StringComparer.Ordinal);

    /// <summary>Bytes the allocation ticks stand for (each about 100 KB of a thread's allocations).</summary>
    public long AllocatedBytes { get; private set; }

    /// <summary>Allocated bytes by the allocated type.</summary>
    public Dictionary<string, long> AllocatedTypes { get; } = new(StringComparer.Ordinal);

    /// <summary>Allocated bytes by the innermost method outside the runtime libraries (the code that asked).</summary>
    public Dictionary<string, long> AllocatingSites { get; } = new(StringComparer.Ordinal);

    /// <summary>Allocated bytes by every method on the stack, once per tick.</summary>
    public Dictionary<string, long> AllocatingInclusive { get; } = new(StringComparer.Ordinal);

    /// <summary>Counts an allocation tick of <paramref name="bytes"/> of <paramref name="type"/> with its <paramref name="frames"/>, innermost first.</summary>
    public void AddAllocation(int thread, long bytes, string type, IReadOnlyList<string> frames)
    {
        Threads.Add(thread);
        AllocatedBytes += bytes;
        AllocatedTypes[type] = AllocatedTypes.GetValueOrDefault(type) + bytes;

        var site = Site(frames);
        AllocatingSites[site] = AllocatingSites.GetValueOrDefault(site) + bytes;

        foreach (var method in frames.Distinct(StringComparer.Ordinal))
        {
            AllocatingInclusive[method] = AllocatingInclusive.GetValueOrDefault(method) + bytes;
        }
    }

    /// <summary>The innermost frame outside the runtime libraries (modules System.*, Microsoft.*, or none known); the innermost when all are.</summary>
    private static string Site(IReadOnlyList<string> frames)
    {
        foreach (var frame in frames)
        {
            if (!frame.StartsWith("System.", StringComparison.Ordinal) && !frame.StartsWith("Microsoft.", StringComparison.Ordinal) && !frame.StartsWith('!'))
            {
                return frame;
            }
        }

        if (frames.Count > 0)
        {
            return frames[0];
        }

        return "?";
    }

    /// <summary>Counts a sample of <paramref name="thread"/> with its <paramref name="frames"/>, innermost first.</summary>
    public void Add(int thread, bool managed, IReadOnlyList<string> frames)
    {
        Threads.Add(thread);

        if (!managed)
        {
            External++;

            return;
        }

        Managed++;

        if (frames.Count == 0)
        {
            return;
        }

        Exclusive[frames[0]] = Exclusive.GetValueOrDefault(frames[0]) + 1;

        foreach (var method in frames.Distinct(StringComparer.Ordinal))
        {
            Inclusive[method] = Inclusive.GetValueOrDefault(method) + 1;
        }
    }
}
