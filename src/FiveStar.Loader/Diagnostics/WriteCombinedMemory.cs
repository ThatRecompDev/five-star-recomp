// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Native;

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// The committed private write-combined memory (protection 0x404: upload heaps, and the driver's own system-memory
/// pools) and how much of it is in 32 MiB regions, for the sample summary: a cold boot's grew from 5 to 12.6 GB after
/// the story load, in 32 MiB regions none of the loader's own buffers make. One walk of the address space (VirtualQueryEx,
/// 0.4 to 1 s at 250,000 regions) per sample, on the sampling thread.
/// </summary>
internal static unsafe class WriteCombinedMemory
{
    // MEMORY_BASIC_INFORMATION (winnt.h): MEM_COMMIT, MEM_PRIVATE, PAGE_READWRITE | PAGE_WRITECOMBINE.
    private const uint CommitState = 0x1000;
    private const uint PrivateType = 0x20000;
    private const uint WriteCombinedReadWrite = 0x404;
    private const long PoolRegionBytes = 32L << 20;
    private const long UserSpaceEnd = 0x7FFF_FFFF_0000;
    private const double BytesPerGigabyte = 1 << 30;

    /// <summary>"write-combined: G GB in N regions, M of 32 MiB".</summary>
    public static string Summary()
    {
        nint process = Kernel32.GetCurrentProcess();
        nint address = 0;
        long bytes = 0;
        int regions = 0;
        int pools = 0;
        MemoryBasicInformation info;

        while (address < UserSpaceEnd && Kernel32.VirtualQueryEx(process, address, &info, (nuint)sizeof(MemoryBasicInformation)) != 0)
        {
            if (info.State == CommitState && info.Type == PrivateType && info.Protect == WriteCombinedReadWrite)
            {
                bytes += (long)info.RegionSize;
                regions++;

                if ((long)info.RegionSize == PoolRegionBytes)
                {
                    pools++;
                }
            }

            address = info.BaseAddress + (nint)info.RegionSize;
        }

        return $"write-combined: {bytes / BytesPerGigabyte:F1} GB in {regions} regions, {pools} of 32 MiB";
    }
}
