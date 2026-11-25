// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// The pure part of <see cref="FourGiBLineWatch"/>: the 4 GiB lines inside an allocation, the dword runs that differ
/// between two snapshots, and the longest run of consecutive values in a change (an index array: what overwrote the
/// game's records at world load, 0x3000c, 0x3000d, ... from a line on).
/// </summary>
internal static class LineChanges
{
    /// <summary>A 4 GiB line: the high dword of an address steps there.</summary>
    public const ulong Line = 1UL << 32;

    private const int Dword = sizeof(uint);

    /// <summary>The 4 GiB lines strictly inside [<paramref name="start"/>, + <paramref name="bytes"/>).</summary>
    public static List<ulong> Lines(ulong start, ulong bytes)
    {
        var lines = new List<ulong>();

        for (ulong line = (start / Line + 1) * Line; line < start + bytes; line += Line)
        {
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>The (byte offset, bytes) runs of dwords that differ, in order.</summary>
    public static List<(int Offset, int Bytes)> Changed(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
    {
        var runs = new List<(int Offset, int Bytes)>();
        int start = -1;
        int length = Math.Min(before.Length, after.Length) / Dword * Dword;

        for (int at = 0; at < length; at += Dword)
        {
            bool differs = !before.Slice(at, Dword).SequenceEqual(after.Slice(at, Dword));

            if (differs && start < 0)
            {
                start = at;
            }
            else if (!differs && start >= 0)
            {
                runs.Add((start, at - start));
                start = -1;
            }
        }

        if (start >= 0)
        {
            runs.Add((start, length - start));
        }

        return runs;
    }

    /// <summary>The most dwords in a row in the run whose values each step by one, and the first of them.</summary>
    public static (int Count, uint First) ConsecutiveValues(ReadOnlySpan<byte> run)
    {
        int best = 0;
        uint first = 0;
        int count = 0;
        uint previous = 0;
        uint start = 0;

        for (int at = 0; at + Dword <= run.Length; at += Dword)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(run[at..]);

            if (count > 0 && value == previous + 1)
            {
                count++;
            }
            else
            {
                count = 1;
                start = value;
            }

            previous = value;

            if (count > best)
            {
                best = count;
                first = start;
            }
        }

        return (best, first);
    }

    /// <summary>"+0x30: 0000000000000000 -> 0c0003000d000300 (12 consecutive values from 0x30000)" for a run, at most <paramref name="shown"/> bytes of each side.</summary>
    public static string Describe(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, (int Offset, int Bytes) run, int shown)
    {
        int bytes = Math.Min(run.Bytes, shown);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"+0x{run.Offset:x} ({run.Bytes} bytes): {Convert.ToHexString(before.Slice(run.Offset, bytes))} -> {Convert.ToHexString(after.Slice(run.Offset, bytes))}");
        var (count, first) = ConsecutiveValues(after.Slice(run.Offset, run.Bytes));

        if (count >= FourGiBLineWatch.IndexRun)
        {
            text.Append(CultureInfo.InvariantCulture, $" ({count} consecutive values from 0x{first:x}: an index array)");
        }

        return text.ToString();
    }
}
