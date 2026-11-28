// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Diagnostics;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// The shader-table guard's read of the NVIDIA driver's two tables, on a fake device laid out as driver 32.0.16.2124's:
/// a table that appears after the first check is watched from then on, and the larger of the two counts (issue #64:
/// table 1 at 492 MiB, table 0 at 226 MiB, no guard line).
/// </summary>
public sealed unsafe class DriverShaderTablesTests
{
    private const ulong DeviceVtable = 0x3d0caa0;
    private const int HolderOffset = 0x21e68;
    private const int EntriesOffset = 0x38;
    private const long EntryBytes = 256;

    [Fact]
    public void The_larger_table_is_read_also_when_it_appears_after_the_first_check()
    {
        const ulong Module = 0x7ff600000000;
        byte* device = (byte*)NativeMemory.AllocZeroed(0x100);
        byte* driver = (byte*)NativeMemory.AllocZeroed(HolderOffset + 8);
        byte* holder = (byte*)NativeMemory.AllocZeroed(0x20);
        byte* table0 = (byte*)NativeMemory.AllocZeroed(0x80);
        byte* table1 = (byte*)NativeMemory.AllocZeroed(0x80);

        try
        {
            DriverShaderTables.Forget();
            *(ulong*)(device + 0x40) = (ulong)driver;
            *(ulong*)driver = Module + DeviceVtable;
            *(ulong*)(driver + HolderOffset) = (ulong)holder;
            *(ulong*)(holder + 8) = (ulong)table0;
            *(uint*)(table0 + EntriesOffset) = 904_000;

            // Table 1 not made yet: table 0 alone counts.
            Assert.Equal(904_000 * EntryBytes, DriverShaderTables.LargestTableBytes((nint)device, Module));

            *(ulong*)(holder + 16) = (ulong)table1;
            *(uint*)(table1 + EntriesOffset) = 2_017_024;
            Assert.Equal(2_017_024 * EntryBytes, DriverShaderTables.LargestTableBytes((nint)device, Module));
        }
        finally
        {
            DriverShaderTables.Forget();
            NativeMemory.Free(device);
            NativeMemory.Free(driver);
            NativeMemory.Free(holder);
            NativeMemory.Free(table0);
            NativeMemory.Free(table1);
        }
    }
}
