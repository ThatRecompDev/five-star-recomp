// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using FiveStar.Formats.Images;
using FiveStar.Loader.Shims.D3D12X.Resources;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// The report side of a <c>--dump-frame</c> readback (<see cref="FrameStageDump"/>): a target whose texels hash as at its
/// previous readback is logged as unchanged and not written; else its PNG through <see cref="StagePixels"/> (or the
/// RGBA8 conversion, or raw bytes), and for an unsigned float target a second, auto-exposed one (<c>-auto.png</c>).
/// Called from <see cref="DrawProbes"/>' report, one thread at a time.
/// </summary>
internal sealed class StageReadback
{
    private const int Channels = 4;
    private const ulong FnvOffset = 14695981039346656037;
    private const ulong FnvPrime = 1099511628211;
    private const string AutoSuffix = "-auto.png";

    private readonly Dictionary<nint, (ulong Hash, string File)> _last = [];
    private int _written;

    /// <summary>The PNGs written so far (the auto-exposed ones not counted).</summary>
    public int Written => Volatile.Read(ref _written);

    /// <summary>The summary of one stage readback of <paramref name="resource"/>, writing <paramref name="png"/> unless unchanged.</summary>
    public string Report(ReadOnlySpan<byte> source, int rowPitch, int width, int rows, int format, bool depth, nint resource, string png)
    {
        ulong hash = Hash(source, rowPitch, width, rows, format);

        if (_last.TryGetValue(resource, out var last) && last.Hash == hash)
        {
            return $"unchanged since {Path.GetFileName(last.File)}, not written";
        }

        _last[resource] = (hash, png);
        int bytes = width * rows * Channels;
        byte[] image = ArrayPool<byte>.Shared.Rent(bytes);

        try
        {
            var target = image.AsSpan(0, bytes);
            string? summary = ReadbackPixels.Summarize(source, rowPitch, width, rows, format, target);
            string? stats = StagePixels.Convert(source, rowPitch, width, rows, format, depth, target, out float auto);

            if (stats is not null)
            {
                summary = (summary ?? $"format {format}") + stats;
            }
            else if (summary is null && StagePixels.Raw(source, rowPitch, width, rows, format, target))
            {
                summary = $"format {format} written as its raw bytes";
            }

            if (summary is null)
            {
                return $"format {format} not converted, not written";
            }

            PngWriter.Write(png, width, rows, target);
            Interlocked.Increment(ref _written);

            return summary + WriteAuto(source, rowPitch, width, rows, format, auto, png, target);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(image);
        }
    }

    /// <summary>The auto-exposed PNG beside <paramref name="png"/> when <paramref name="exposure"/> is above 0; what to add to the summary.</summary>
    private static string WriteAuto(ReadOnlySpan<byte> source, int rowPitch, int width, int rows, int format, float exposure, string png, Span<byte> image)
    {
        if (exposure <= 0 || !StagePixels.Expose(source, rowPitch, width, rows, format, exposure, image))
        {
            return string.Empty;
        }

        string path = AutoPath(png);
        PngWriter.Write(path, width, rows, image);

        return string.Create(CultureInfo.InvariantCulture, $"; auto exposure {exposure:G4} in {Path.GetFileName(path)}");
    }

    /// <summary>"x.png" as "x-auto.png".</summary>
    public static string AutoPath(string png)
    {
        return Path.ChangeExtension(png, null) + AutoSuffix;
    }

    /// <summary>FNV-1a over each row's texel bytes (the whole pitch for a block format), not the padding after them.</summary>
    public static ulong Hash(ReadOnlySpan<byte> source, int rowPitch, int width, int rows, int format)
    {
        int texel = StagePixels.TexelBytes(format);
        int rowBytes = texel == 0 ? rowPitch : Math.Min(rowPitch, width * texel);
        ulong hash = FnvOffset;

        for (int y = 0; y < rows; y++)
        {
            var row = source.Slice(y * rowPitch, Math.Min(rowBytes, source.Length - (y * rowPitch)));
            var words = MemoryMarshal.Cast<byte, ulong>(row);

            foreach (ulong word in words)
            {
                hash = (hash ^ word) * FnvPrime;
            }

            foreach (byte tail in row[(words.Length * sizeof(ulong))..])
            {
                hash = (hash ^ tail) * FnvPrime;
            }
        }

        return hash;
    }
}
