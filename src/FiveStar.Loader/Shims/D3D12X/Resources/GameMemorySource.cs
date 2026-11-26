// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// Where the CPU reads console GPU memory: an address in a GPU-only alias goes to its CPU range
/// (<see cref="XMemPhysicalMemory.CpuAddress"/>); in an XMem reservation every 64 KiB page of the range must be mapped,
/// elsewhere every byte must be committed and readable (memory the game committed itself). Reading anything else
/// would fault, so such a range has no source.
/// </summary>
internal sealed unsafe class GameMemorySource
{
    /// <summary>PAGE_NOACCESS and PAGE_GUARD: committed pages the CPU cannot read (winnt.h).</summary>
    private const uint UnreadableProtection = Win32Constants.PageNoAccess | Win32Constants.PageGuard;

    private readonly XMemPhysicalMemory _memory;

    public GameMemorySource(XMemPhysicalMemory memory)
    {
        _memory = memory;
    }

    /// <summary>A CPU pointer to <paramref name="size"/> readable bytes at console GPU address <paramref name="address"/>, or null.</summary>
    [HotPath]
    public byte* Find(ulong address, ulong size)
    {
        if (address == 0 || size == 0)
        {
            return null;
        }

        nint cpu = _memory.CpuAddress((nint)address);
        bool readable = _memory.IsReserved(cpu) ? AllMapped(cpu, size) : AllCommitted(cpu, size);

        if (!readable)
        {
            return null;
        }

        return (byte*)cpu;
    }

    /// <summary>Whether console GPU address <paramref name="address"/> lies in an XMem reservation (memory the game maps pages into, now or later).</summary>
    public bool InReservation(ulong address)
    {
        return address != 0 && _memory.IsReserved(_memory.CpuAddress((nint)address));
    }

    /// <summary>
    /// Copies <paramref name="size"/> bytes at console GPU address <paramref name="address"/> of an XMem reservation into
    /// <paramref name="destination"/> page by page, through the kernel (a page may be unmapped meanwhile): each 64 KiB
    /// page from where a frame is mapped for it, the CPU range or the GPU-only alias itself, and zeros for a page mapped
    /// nowhere (a mip the game has not streamed in; the console reads such pages as zeros). Returns the pages copied.
    /// </summary>
    public long Gather(ulong address, ulong size, byte* destination)
    {
        ulong pageSize = XMemPhysicalMemory.PageSize;
        nint process = Kernel32.GetCurrentProcess();
        long copied = 0;

        for (ulong done = 0; done < size;)
        {
            ulong at = address + done;
            ulong length = Math.Min(size - done, pageSize - (at % pageSize));
            nint source = MappedAt((nint)at);
            nuint read = 0;

            if (source == 0 || Kernel32.ReadProcessMemory(process, source, destination + done, (nuint)length, &read) == 0 || read != (nuint)length)
            {
                new Span<byte>(destination + done, (int)length).Clear();
            }
            else
            {
                copied++;
            }

            done += length;
        }

        return copied;
    }

    /// <summary>"CPU range 0x…: m of n pages mapped there, k at the address": where a texture's pages are mapped, for a log line.</summary>
    public string Describe(ulong address)
    {
        nint cpu = _memory.CpuAddress((nint)address);

        return $"CPU range 0x{cpu:x}, reserved {_memory.IsReserved(cpu)}, its first page mapped {_memory.IsMapped(cpu)}, at the address {_memory.IsMapped((nint)address)}";
    }

    /// <summary>Where the CPU reads the page holding <paramref name="address"/>: its CPU range when mapped there, else the address when its own range maps it; 0 for neither.</summary>
    private nint MappedAt(nint address)
    {
        nint cpu = _memory.CpuAddress(address);

        if (_memory.IsMapped(cpu))
        {
            return cpu;
        }

        if (cpu != address && _memory.IsMapped(address))
        {
            return address;
        }

        return 0;
    }

    private bool AllMapped(nint start, ulong size)
    {
        ulong pageSize = XMemPhysicalMemory.PageSize;
        ulong first = (ulong)start / pageSize * pageSize;

        for (ulong page = first; page < (ulong)start + size; page += pageSize)
        {
            if (!_memory.IsMapped((nint)page))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether every byte is committed and readable. An allocation committed in full with a readable protection answers
    /// at once (the game's heaps; it does not change protections inside them); anything else is walked with VirtualQuery,
    /// which costs milliseconds in a large allocation, and the game asks here per Map and per watched texture a frame.
    /// </summary>
    [HotPath]
    private static bool AllCommitted(nint start, ulong size)
    {
        if (MemoryRegionInformation.Query(start, out var region) && region.FullyCommittedPrivate && region.Holds(start, size)
            && region.AllocationProtect != 0 && (region.AllocationProtect & UnreadableProtection) == 0)
        {
            return true;
        }

        nint process = Kernel32.GetCurrentProcess();
        nint end = start + (nint)size;
        nint next = start;
        MemoryBasicInformation info;

        while (next < end)
        {
            if (Kernel32.VirtualQueryEx(process, next, &info, (nuint)sizeof(MemoryBasicInformation)) == 0)
            {
                return false;
            }

            if (info.State != Win32Constants.MemCommit || (info.Protect & UnreadableProtection) != 0)
            {
                return false;
            }

            next = info.BaseAddress + (nint)info.RegionSize;
        }

        return true;
    }
}
