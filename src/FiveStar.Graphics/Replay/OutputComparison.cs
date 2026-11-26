// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;

namespace FiveStar.Graphics.Replay;

/// <summary>
/// Compares a replay's output with a capture's reference, dword by dword over the bytes that hold data (a texture's
/// rows, not their padding): bit for bit, or with a tolerance on the dwords read as 32-bit floats (NaN equals NaN).
/// </summary>
public static class OutputComparison
{
    private const int DwordBytes = sizeof(uint);

    /// <summary>The differences of <paramref name="actual"/> from <paramref name="expected"/>, the first <paramref name="shown"/> offsets kept.</summary>
    public static OutputDifference Compare(CapturedResource resource, ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, float? tolerance, int shown)
    {
        long differing = 0;
        ulong compared = 0;
        var offsets = new List<ulong>();

        foreach (var (start, length) in Spans(resource))
        {
            for (ulong offset = start; offset < start + length; offset += DwordBytes)
            {
                var size = (int)Math.Min(DwordBytes, start + length - offset);
                compared += (ulong)size;

                if (Same(expected.Slice((int)offset, size), actual.Slice((int)offset, size), tolerance))
                {
                    continue;
                }

                differing++;

                if (offsets.Count < shown)
                {
                    offsets.Add(offset);
                }
            }
        }

        return new OutputDifference(resource.Id, differing, offsets, compared);
    }

    /// <summary>The byte ranges with data: a buffer whole, a texture's rows by its footprints.</summary>
    public static IEnumerable<(ulong Start, ulong Length)> Spans(CapturedResource resource)
    {
        if (resource.IsBuffer || resource.Footprints is null)
        {
            yield return (0, resource.Bytes);

            yield break;
        }

        foreach (var footprint in resource.Footprints)
        {
            for (ulong row = 0; row < (ulong)footprint.Rows * footprint.Depth; row++)
            {
                yield return (footprint.Offset + (row * footprint.RowPitch), footprint.RowBytes);
            }
        }
    }

    private static bool Same(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, float? tolerance)
    {
        if (expected.SequenceEqual(actual))
        {
            return true;
        }

        if (tolerance is null || expected.Length != DwordBytes)
        {
            return false;
        }

        var left = BinaryPrimitives.ReadSingleLittleEndian(expected);
        var right = BinaryPrimitives.ReadSingleLittleEndian(actual);

        if (float.IsNaN(left) || float.IsNaN(right))
        {
            return float.IsNaN(left) && float.IsNaN(right);
        }

        return Math.Abs(left - right) <= tolerance.Value;
    }
}
