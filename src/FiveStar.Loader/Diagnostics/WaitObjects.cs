// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using FiveStar.Loader.Loading;

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// The game's events and semaphores and the threads that wait on them (<c>--watch-hang</c>, through
/// <see cref="Shims.WaitObjectShims"/>): who created each object (the game frames of the creating call), when it was
/// last signaled and by which thread, and which object each blocked thread waits on since when. A lost wake-up, a
/// wait whose object nobody signals any more, then names the object and its creator instead of only the waiting stack.
/// </summary>
internal static unsafe class WaitObjects
{
    /// <summary>The game frames kept per creation and per wait.</summary>
    private const int Frames = 6;

    /// <summary>Waits shorter than this are not reported.</summary>
    private const double ReportedSeconds = 1;

    // winbase.h: WAIT_OBJECT_0, WAIT_TIMEOUT and WAIT_ABANDONED_0.
    private const uint WaitTimeout = 0x102;
    private const uint WaitAbandoned0 = 0x80;

    /// <summary>MAXIMUM_WAIT_OBJECTS (winnt.h).</summary>
    private const uint MaximumWaitObjects = 64;

    private static readonly ConcurrentDictionary<nint, WaitObject> Objects = new();
    private static readonly ConcurrentDictionary<uint, ThreadWait> Waits = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static double Now => Clock.Elapsed.TotalSeconds;

    /// <summary>A created object: its kind, the creating thread and the return addresses of the creating call.</summary>
    public static void Created(nint handle, string kind)
    {
        if (handle == 0)
        {
            return;
        }

        Objects[handle] = new WaitObject(kind, Native.Kernel32.GetCurrentThreadId(), Now, GameFrames(Frames));
    }

    /// <summary>The object was set or released by the calling thread.</summary>
    public static void Signaled(nint handle)
    {
        if (Objects.TryGetValue(handle, out var item))
        {
            item.Signal(Native.Kernel32.GetCurrentThreadId(), Now, Caller());
        }
    }

    /// <summary>The event was reset by the calling thread: a signal nobody waited for is gone.</summary>
    public static void Reset(nint handle)
    {
        Record(handle, "reset");
    }

    /// <summary>The calling thread starts waiting on any (or all, <paramref name="all"/>) of <paramref name="handles"/>.</summary>
    public static void WaitingOnSeveral(ReadOnlySpan<nint> handles, bool all, uint milliseconds)
    {
        if (handles.IsEmpty)
        {
            return;
        }

        var frames = GameFrames(Frames);
        Waits[Native.Kernel32.GetCurrentThreadId()] = new ThreadWait(handles[0], milliseconds, Now, frames) { Several = handles.ToArray(), All = all };
        string verb = $"wait on any of {handles.Length}";

        if (all)
        {
            verb = $"wait on all of {handles.Length}";
        }

        foreach (var handle in handles)
        {
            Record(handle, verb, First(frames));
        }
    }

    /// <summary>
    /// A wait on any (or all, <paramref name="all"/>) of <paramref name="handles"/> returned <paramref name="result"/>: the
    /// object that woke it gets the wake-up (each one for a wait on all), an abandoned mutex its abandonment, and every
    /// object a timeout or a failure.
    /// </summary>
    public static void DoneOnSeveral(ReadOnlySpan<nint> handles, bool all, uint result)
    {
        Waits.TryRemove(Native.Kernel32.GetCurrentThreadId(), out _);
        nint caller = Caller();

        if (result < handles.Length)
        {
            if (!all)
            {
                Record(handles[(int)result], "woke", caller);

                return;
            }
        }
        else if (result - WaitAbandoned0 < handles.Length)
        {
            if (!all)
            {
                Record(handles[(int)(result - WaitAbandoned0)], "abandoned", caller);

                return;
            }
        }

        string verb = Outcome(result);

        foreach (var handle in handles)
        {
            Record(handle, verb, caller);
        }
    }

    /// <summary>The handle was closed: a later object may get the same value.</summary>
    public static void Closed(nint handle)
    {
        Objects.TryRemove(handle, out _);
    }

    /// <summary>The calling thread starts waiting on <paramref name="handle"/> for <paramref name="milliseconds"/>.</summary>
    public static void Waiting(nint handle, uint milliseconds)
    {
        var frames = GameFrames(Frames);
        Waits[Native.Kernel32.GetCurrentThreadId()] = new ThreadWait(handle, milliseconds, Now, frames);
        Record(handle, "wait", First(frames));
    }

    /// <summary>The calling thread's wait on <paramref name="handle"/> returned <paramref name="result"/> (WAIT_OBJECT_0, WAIT_TIMEOUT, ..).</summary>
    public static void Done(nint handle, uint result)
    {
        Waits.TryRemove(Native.Kernel32.GetCurrentThreadId(), out _);
        Record(handle, Outcome(result));
    }

    private static string Outcome(uint result)
    {
        if (result == WaitTimeout)
        {
            return "timed out";
        }

        if (result >= WaitAbandoned0 && result < WaitAbandoned0 + MaximumWaitObjects)
        {
            return "abandoned";
        }

        if (result < MaximumWaitObjects)
        {
            return "woke";
        }

        return $"wait returned 0x{result:x}";
    }

    private static void Record(nint handle, string verb)
    {
        Record(handle, verb, Caller());
    }

    private static void Record(nint handle, string verb, nint caller)
    {
        if (Objects.TryGetValue(handle, out var item))
        {
            item.Record(new WaitOperation(verb, Native.Kernel32.GetCurrentThreadId(), Now, caller));
        }
    }

    /// <summary>
    /// The game's innermost return address above the shim, 0 when none was found. One frame only: it is taken on every
    /// signal and wake-up, the very calls whose timing the hang watch is watching.
    /// </summary>
    private static nint Caller()
    {
        return First(GameFrames(1));
    }

    private static nint First(nint[] frames)
    {
        if (frames.Length == 0)
        {
            return 0;
        }

        return frames[0];
    }

    /// <summary>
    /// One line per thread blocked for a second or more in a wait the shims saw, longest first: the object, its
    /// creator and its last signal. Empty when there is none.
    /// </summary>
    public static string Report(ImageTable images, string prefix)
    {
        return Report(images, prefix, ReportedSeconds);
    }

    /// <summary>The same for waits of <paramref name="seconds"/> or more.</summary>
    public static string Report(ImageTable images, string prefix, double seconds)
    {
        double now = Now;
        var text = new StringBuilder();

        foreach (var (thread, wait) in Waits.ToArray().OrderBy(pair => pair.Value.Since))
        {
            double waited = now - wait.Since;

            if (waited < seconds)
            {
                continue;
            }

            text.Append(CultureInfo.InvariantCulture, $"\n{prefix}  thread {thread} waits {waited:F1} s ({Timeout(wait.Milliseconds)}) on {Target(wait)}: ");

            if (Objects.TryGetValue(wait.Handle, out var item))
            {
                text.Append(item.Describe(images, now));
            }
            else
            {
                text.Append(NotSeen(wait.Handle));
            }

            text.Append(CultureInfo.InvariantCulture, $"; waiting at {WaitObject.Frames(wait.Frames, images)}");
        }

        if (text.Length == 0)
        {
            return string.Empty;
        }

        return $"{prefix}threads blocked on the game's events and semaphores:{text}";
    }

    /// <summary>What a wait is on: its handle, or for a wait on several which of them (the first is described).</summary>
    private static string Target(ThreadWait wait)
    {
        var several = wait.Several;

        if (several is null || several.Length <= 1)
        {
            return $"handle 0x{wait.Handle:x}";
        }

        string which = wait.All ? "all" : "one";
        string handles = string.Join(", ", several.Select(handle => $"0x{handle:x}"));

        return $"{which} of {several.Length} handles ({handles}), the first 0x{wait.Handle:x}";
    }

    /// <summary>A handle the shims did not see created: a thread's (a join on it), or another object.</summary>
    private static string NotSeen(nint handle)
    {
        uint thread = Native.Kernel32.GetThreadId(handle);

        if (thread != 0)
        {
            return $"the handle of thread {thread} (waiting for it to end)";
        }

        return "not created through the shims";
    }

    private static string Timeout(uint milliseconds)
    {
        if (milliseconds == uint.MaxValue)
        {
            return "no timeout";
        }

        return $"timeout {milliseconds} ms";
    }

    /// <summary>The game frames of the calling thread above the shim, innermost first (<see cref="ReturnAddresses"/>); none without a runtime.</summary>
    private static nint[] GameFrames(int count)
    {
        var runtime = Shims.ShimContext.ActiveRuntime;

        if (runtime is null)
        {
            return [];
        }

        return ReturnAddresses.Scan(runtime.Images, count);
    }
}
