// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// A fault record shows the memory a register points at and its allocation: a pointer the game read from corrupted
/// memory (packed id pairs at world load) then shows what overwrote it, and whose memory that is.
/// </summary>
public sealed unsafe class FaultPointedMemoryTests
{
    private const ulong IdPair = 0x0003201100032010;
    private const nuint Page = 0x1000;

    [Fact]
    public void A_register_pointing_into_memory_shows_the_bytes_there_and_its_allocation()
    {
        nint block = Kernel32.VirtualAlloc(0, Page, Win32Constants.MemReserveCommit, Win32Constants.PageReadWrite);

        try
        {
            nint entry = block + 0x840;
            *(ulong*)entry = IdPair;

            string? line = FaultRecordWriter.Pointed(entry);

            Assert.NotNull(line);
            Assert.StartsWith($"0x{entry:x} in allocation 0x{block:x} (+0x840): ", line, StringComparison.Ordinal);
            Assert.Contains("| 1020030011200300", line, StringComparison.Ordinal);
            Assert.Null(FaultRecordWriter.Pointed(unchecked((nint)0x0003201300032012)));
        }
        finally
        {
            _ = Kernel32.VirtualFree(block, 0, Win32Constants.MemRelease);
        }
    }
}
