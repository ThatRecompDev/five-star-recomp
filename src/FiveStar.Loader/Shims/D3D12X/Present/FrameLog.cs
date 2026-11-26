// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace FiveStar.Loader.Shims.D3D12X.Present;

/// <summary>
/// <c>--frame-log &lt;every&gt;</c> (issue #39): every <see cref="Every"/>th frame, and every frame of 250 ms or more, the
/// CPU side of the frame in one line: the time between two presents split into the game's threads in our submit path
/// (ExecuteCommandLists, with the texture refresh and uploads it does first), descriptor-table mirroring at binds, the
/// runtime's GC pauses and the managed bytes allocated (what drives them), the rest of the game's own time, and the present itself (latency wait, retire wait, copy
/// submission, IDXGISwapChain::Present). Submit and mirroring are summed over every thread that ran them, so on a game
/// that records on several threads they can pass the frame's length. The GPU side is <c>--gpu-timings</c>. After
/// VIctory's frame log (VICTORY_FRAME_LOG; facts only, GPL-3.0), which found that fork's render thread waiting in its
/// submit path for 257 of 292 ms a frame. Off by default: then the counters are not taken.
/// </summary>
internal static class FrameLog
{
    /// <summary>Every how many frames a line is logged; 0 is off.</summary>
    public static int Every { get; set; }

    public static bool Enabled => Every > 0;

    private static long _submitTicks;
    private static long _refreshTicks;
    private static long _listWaitTicks;
    private static long _preludeTicks;

    /// <summary>A start mark for <see cref="AddSubmit"/> or <see cref="AddRefresh"/>; 0 when off.</summary>
    [HotPath]
    public static long Start()
    {
        if (Every <= 0)
        {
            return 0;
        }

        return Stopwatch.GetTimestamp();
    }

    /// <summary>One ExecuteCommandLists through our path ended (started at <paramref name="start"/>).</summary>
    [HotPath]
    public static void AddSubmit(long start)
    {
        if (start != 0)
        {
            Interlocked.Add(ref _submitTicks, Stopwatch.GetTimestamp() - start);
        }
    }

    /// <summary>The texture refresh and uploads at the start of an ExecuteCommandLists ended.</summary>
    [HotPath]
    public static void AddRefresh(long start)
    {
        if (start != 0)
        {
            Interlocked.Add(ref _refreshTicks, Stopwatch.GetTimestamp() - start);
        }
    }

    /// <summary>
    /// A submission blocked on the CPU until the GPU finished the list's previous run
    /// (<see cref="ListSubmissions.WaitIfRunning"/>): part of the submit path, the GPU's backlog rather than our work.
    /// </summary>
    [HotPath]
    public static void AddListWait(long start)
    {
        if (start != 0)
        {
            Interlocked.Add(ref _listWaitTicks, Stopwatch.GetTimestamp() - start);
        }
    }

    /// <summary>A depth-twin prelude (recorded and submitted with its two fence waits) ended; part of the submit path.</summary>
    [HotPath]
    public static void AddPrelude(long start)
    {
        if (start != 0)
        {
            Interlocked.Add(ref _preludeTicks, Stopwatch.GetTimestamp() - start);
        }
    }

    /// <summary>The submit path's sums since the last call, in ticks; starts the next frame's sums.</summary>
    public static SubmitTicks Take()
    {
        return new SubmitTicks(Interlocked.Exchange(ref _submitTicks, 0), Interlocked.Exchange(ref _refreshTicks, 0), Interlocked.Exchange(ref _listWaitTicks, 0), Interlocked.Exchange(ref _preludeTicks, 0));
    }

    /// <summary>
    /// "[frame] present N: X ms = game G (our submit path S, of it texture refresh R, waits for lists' previous runs W,
    /// depth-twin preludes D; descriptor mirroring M; GC C in a/b/c collections of gen 0/1/2, A MB allocated) +
    /// present P (latency L, retire T, submit U, present V)", all in milliseconds.
    /// </summary>
    public static string Line(long presents, FrameTimes times)
    {
        var c = CultureInfo.InvariantCulture;

        return string.Create(c, $"  [frame] present {presents}: {times.Frame:F1} ms = game {times.Game:F1} (our submit path {times.Submit:F1}, of it texture refresh {times.Refresh:F1}, waits for lists' previous runs {times.ListWaits:F1}, depth-twin preludes {times.Preludes:F1}; descriptor mirroring {times.Mirroring:F1}; GC pauses {times.Collections:F1} in {times.Gen0}/{times.Gen1}/{times.Gen2} collections of gen 0/1/2, {times.AllocatedMegabytes:F1} MB allocated) + present {times.Present:F1} (latency wait {times.Latency:F1}, retire wait {times.Retire:F1}, copy submit {times.CopySubmit:F1}, Present {times.Swap:F1})");
    }

    /// <summary>Whether frame <paramref name="presents"/> of <paramref name="milliseconds"/> gets a line.</summary>
    public static bool Logs(long presents, double milliseconds, double slowMilliseconds)
    {
        return Every > 0 && (presents % Every == 0 || milliseconds >= slowMilliseconds);
    }
}
