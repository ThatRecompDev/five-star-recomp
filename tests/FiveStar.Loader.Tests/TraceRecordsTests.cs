// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>--d3d12-trace-records: the window records a program reads through user SGPR pointers.</summary>
public sealed unsafe class TraceRecordsTests
{
    private const uint WindowBytes = 0x1000;
    private const uint Table = 0x200;

    [Fact]
    public void Records_at_sgpr_pointers_show_a_V_sharp_base_and_values_a_T_sharp_base_and_a_heap_index()
    {
        var offsets = TraceRecords.Parse("0x20, 64 ,0x60");
        Assert.Equal([0x20u, 0x40u, 0x60u], offsets);

        byte* window = (byte*)NativeMemory.AllocZeroed(WindowBytes);
        uint* constants = (uint*)NativeMemory.AllocZeroed(TraceRecords.BaseDwords * sizeof(uint));

        try
        {
            // +0x20: a V# at console 0x284_8a43b440, 4336 records; +0x40: a T# (type 9) at 0x12_3456_7800; +0x60: a heap index.
            Write(window + Table + 0x20, [0x8a43b440, 0x284, 0x10f0, 0x31014fac]);
            Write(window + Table + 0x40, [0x12345678, 0x00000000, 0, 0x90000000]);
            Write(window + Table + 0x60, [417733, 0, 1, 0x11014fac]);
            constants[0] = 0x40000000;
            constants[2] = 0xbf800000;

            var text = new StringBuilder();
            uint[] sgprs = [Table, 0x123, 0, Table, WindowBytes];
            var mapped = new Dictionary<ulong, string> { [0x2848a43b440] = "mapped" };
            var memory = new Dictionary<ulong, nint> { [0x2848a43b440] = (nint)constants };
            TraceRecords.Append(text, window, WindowBytes, sgprs, offsets, address => mapped.GetValueOrDefault(address, "UNMAPPED"), (address, _) => memory.GetValueOrDefault(address));
            string log = text.ToString();

            // Only sgpr 0 is a usable window offset: 0x123 is not record-aligned, sgpr 3 repeats it, 0x1000 has no room.
            Assert.Equal(3, log.Split("\n      sgpr ").Length - 1);
            Assert.Contains("sgpr 0 0x200 +0x20: 8a43b440 00000284 000010f0 31014fac", log, StringComparison.Ordinal);
            Assert.Contains("V# base 0x2848a43b440, 4336 records: mapped; first dwords: 40000000 00000000 bf800000", log, StringComparison.Ordinal);
            Assert.Contains("T# base 0x1234567800: UNMAPPED", log, StringComparison.Ordinal);
            Assert.Contains("V# holds heap index 417733, no console base", log, StringComparison.Ordinal);
        }
        finally
        {
            NativeMemory.Free(window);
            NativeMemory.Free(constants);
        }
    }

    private static void Write(byte* at, uint[] dwords)
    {
        dwords.CopyTo(new Span<uint>(at, dwords.Length));
    }
}
