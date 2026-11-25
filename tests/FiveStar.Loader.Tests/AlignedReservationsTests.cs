// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Native;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// XMemVirtualAlloc memory sits on a 4 GiB line, as on the console: the game rebuilds addresses in it as the base's
/// high dword with a 32-bit offset, which only lands inside the allocation when the base's low dword is 0.
/// </summary>
public sealed class AlignedReservationsTests
{
    private const nuint Bytes = 0x20000;
    private const uint Offset = 0xc000;

    [Theory]
    [InlineData(Win32Constants.MemReserveCommit, Win32Constants.PageReadWrite)]
    [InlineData(Win32Constants.MemReserve | MemoryConstants.MemReservePlaceholder, Win32Constants.PageNoAccess)]
    public void An_allocation_starts_on_a_4_GiB_line_so_high_dword_and_offset_name_its_bytes(uint type, uint protect)
    {
        nint process = Kernel32.GetCurrentProcess();
        nint range = AlignedReservations.Allocate(process, Bytes, type, protect);

        try
        {
            Assert.NotEqual(0, range);
            Assert.Equal(0UL, (ulong)range % AlignedReservations.Alignment);

            // w75: the game's pointer 0x1ce0000c000 meant its allocation's base + 0xc000.
            ulong rebuilt = ((ulong)range & ~(AlignedReservations.Alignment - 1)) | Offset;
            Assert.Equal((ulong)range + Offset, rebuilt);
        }
        finally
        {
            _ = Kernel32.VirtualFree(range, 0, Win32Constants.MemRelease);
        }
    }
}
