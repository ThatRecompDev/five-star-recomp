// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Native;

namespace FiveStar.Loader.Diagnostics;

/// <summary>The process's memory for the sample summary: its private commit and the parts the loader can name.</summary>
internal static unsafe class ProcessMemory
{
    private const double BytesPerGigabyte = 1 << 30;

    /// <summary>
    /// Private commit, the managed heap, the game's XMem allocations through the loader and the physical pages it
    /// holds (pagefile-backed sections, so commit but not private bytes).
    /// </summary>
    public static string Summary(long xmemBytes, long physicalBytes)
    {
        ProcessMemoryCountersEx counters = default;
        counters.Size = (uint)sizeof(ProcessMemoryCountersEx);
        ulong privateBytes = 0;

        if (Kernel32.K32GetProcessMemoryInfo(Kernel32.GetCurrentProcess(), &counters, counters.Size) != 0)
        {
            privateBytes = counters.PrivateUsage;
        }

        return $"memory: private {Gigabytes((long)privateBytes)} ({Gigabytes(WriteCombinedMemory.CachedBytes)} of it write-combined: the driver's mapping of video memory, not RAM), managed heap {Gigabytes(GC.GetTotalMemory(false))}, XMem allocations {Gigabytes(xmemBytes)}, physical pages {Gigabytes(physicalBytes)}";
    }

    private static string Gigabytes(long bytes)
    {
        return $"{bytes / BytesPerGigabyte:F1} GB";
    }
}
