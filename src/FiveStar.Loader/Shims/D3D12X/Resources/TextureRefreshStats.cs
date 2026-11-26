// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// <c>--frame-log</c>: where the texture refresh's time goes between two frame lines, on whichever thread ran it (the
/// refresh worker's, or a submission's with <c>--texture-refresh-wait</c>): reading and hashing memory, detiling and
/// submitting uploads, and a submission's wait for refreshes; with the textures refreshed (written, watched) and the
/// bytes read and uploaded. w113's frame line had the refresh at 59-124 ms of the submit path with no split of it.
/// </summary>
internal static class TextureRefreshStats
{
    private const double BytesPerMegabyte = 1 << 20;
    private const double MillisecondsPerSecond = 1000.0;

    private static long _readTicks;
    private static long _uploadTicks;
    private static long _waitTicks;
    private static long _firstWaitTicks;
    private static long _firsts;
    private static long _written;
    private static long _watched;
    private static long _bytesRead;
    private static long _bytesUploaded;

    /// <summary>Read and hash time of a refresh that started at <paramref name="start"/> (<see cref="Stopwatch.GetTimestamp"/>), and the bytes read.</summary>
    public static void AddRead(long start, ulong bytes)
    {
        Interlocked.Add(ref _readTicks, Stopwatch.GetTimestamp() - start);
        Interlocked.Add(ref _bytesRead, (long)bytes);
    }

    /// <summary>An upload (detile, record and submit) that started at <paramref name="start"/>, of <paramref name="bytes"/> bytes of memory.</summary>
    public static void AddUpload(long start, ulong bytes)
    {
        Interlocked.Add(ref _uploadTicks, Stopwatch.GetTimestamp() - start);
        Interlocked.Add(ref _bytesUploaded, (long)bytes);
    }

    /// <summary>A submission waited for refreshes from <paramref name="start"/>.</summary>
    public static void AddWait(long start)
    {
        Interlocked.Add(ref _waitTicks, Stopwatch.GetTimestamp() - start);
    }

    /// <summary>A submission waited from <paramref name="start"/> for the first fills of textures made before it.</summary>
    public static void AddFirstWait(long start)
    {
        Interlocked.Add(ref _firstWaitTicks, Stopwatch.GetTimestamp() - start);
    }

    /// <summary>A new texture's first fill ran (off the thread that made it).</summary>
    public static void AddFirst()
    {
        Interlocked.Increment(ref _firsts);
    }

    /// <summary>A written texture (a DirectStorage write, a page mapped, a view's check) was refreshed.</summary>
    public static void AddWritten()
    {
        Interlocked.Increment(ref _written);
    }

    /// <summary>A watched (CPU-mapped) texture was checked.</summary>
    public static void AddWatched()
    {
        Interlocked.Increment(ref _watched);
    }

    /// <summary>
    /// "[frame] texture refresh: read+hash R ms, upload U ms, submissions waited W ms (first fills F ms); N first fills,
    /// T written, C watched; X MB read, Y MB uploaded"; the sums start over.
    /// </summary>
    public static string Line()
    {
        double read = Milliseconds(Interlocked.Exchange(ref _readTicks, 0));
        double upload = Milliseconds(Interlocked.Exchange(ref _uploadTicks, 0));
        double wait = Milliseconds(Interlocked.Exchange(ref _waitTicks, 0));
        double firstWait = Milliseconds(Interlocked.Exchange(ref _firstWaitTicks, 0));

        long firsts = Interlocked.Exchange(ref _firsts, 0);
        long written = Interlocked.Exchange(ref _written, 0);
        long watched = Interlocked.Exchange(ref _watched, 0);
        double megabytesRead = Interlocked.Exchange(ref _bytesRead, 0) / BytesPerMegabyte;
        double megabytesUploaded = Interlocked.Exchange(ref _bytesUploaded, 0) / BytesPerMegabyte;

        return string.Create(CultureInfo.InvariantCulture, $"  [frame] texture refresh: read+hash {read:F1} ms, upload {upload:F1} ms, submissions waited {wait:F1} ms (first fills {firstWait:F1} ms); {firsts} first fills, {written} written, {watched} watched; {megabytesRead:F1} MB read, {megabytesUploaded:F1} MB uploaded");
    }

    private static double Milliseconds(long ticks)
    {
        return ticks * MillisecondsPerSecond / Stopwatch.Frequency;
    }
}
