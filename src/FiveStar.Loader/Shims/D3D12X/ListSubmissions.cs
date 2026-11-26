// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// The console runs a closed command list again while its previous submission is still executing: the game records
/// a few lists once at device creation (X slots 131 and 132, 0x142478ba4) and submits one of them with every present
/// (0x142482660). The desktop does not allow that (the debug layer drops the submission, error #553), so each queue
/// signals a fence of its own after every submission, each list remembers that value, and a list still in flight
/// is waited for on the CPU before it runs again.
/// <para>
/// A list reset since that submission runs a new recording, which the desktop allows while the last one still runs
/// (the recording lives in its allocator, which the game recycles only once the GPU is done). The game recycles its
/// lists that way, and the CPU wait was most of the world frame's submit path (w98: 90-570 ms a frame, the GPU idle
/// 544-812 ms of 874-1457). Only the loader's own per-list buffers must not be shared by the two runs (the argument
/// scratch, <see cref="ListIndirectScratch"/>, which Reset starts over): on the same queue the queue's order keeps the
/// runs apart, and another queue waits for the last run on the GPU.
/// </para>
/// </summary>
internal static unsafe class ListSubmissions
{
    /// <summary>ID3D12Fence::GetCompletedValue and ::SetEventOnCompletion; ID3D12CommandQueue::Signal and ::Wait (d3d12.h).</summary>
    private const int GetCompletedValueSlot = 8;
    private const int SetEventOnCompletionSlot = 9;
    private const int QueueSignalSlot = 14;
    private const int QueueWaitSlot = 15;

    /// <summary>ID3D12Device::CreateFence.</summary>
    private const int CreateFenceSlot = 36;

    private static readonly Guid FenceIid = new("0a753dcf-c4d8-4b91-adf6-be5a60d95a76");

    private const long LongWaitMilliseconds = 100;
    private const long LongWaitsLogged = 16;

    private static long _waits;
    private static long _queueWaits;
    private static long _inOrder;
    private static long _longWaits;

    /// <summary>The waits for lists' previous runs, for the sample line and the device's summary.</summary>
    public static string Summary =>
        $"{Interlocked.Read(ref _waits)} submissions waited on the CPU for the list's previous run; new recordings while it ran: {Interlocked.Read(ref _queueWaits)} waited for it on the GPU (another queue), {Interlocked.Read(ref _inOrder)} ran after it in their queue's order";

    /// <summary>
    /// Before a submission to <paramref name="queue"/>: when the previous run of <paramref name="list"/> (a wrapper) has
    /// not finished, a new recording runs after it (<see cref="After"/>), and the same recording blocks until it has.
    /// </summary>
    [HotPath]
    public static void WaitIfRunning(nint list, XboxQueueObject* queue)
    {
        var submission = ListSubmission.Of(list);
        nint fence = submission->Fence;

        if (fence == 0 || Completed(fence) >= submission->Value)
        {
            return;
        }

        if (submission->ResetSince != 0)
        {
            After(queue, fence, submission->Value);

            return;
        }

        Interlocked.Increment(ref _waits);
        long mark = Present.FrameLog.Start();
        long start = Environment.TickCount64;
        MemoryWaits.ReleaseBehind(fence);

        // A null event makes SetEventOnCompletion return once the value is reached (or the device is removed).
        _ = ((delegate* unmanaged<nint, ulong, nint, int>)ComObject.Slot(fence, SetEventOnCompletionSlot))(fence, submission->Value, 0);
        long waited = Environment.TickCount64 - start;
        Present.FrameLog.AddListWait(mark);

        if (waited >= LongWaitMilliseconds && Interlocked.Increment(ref _longWaits) <= LongWaitsLogged)
        {
            WarnLongWait(list, waited);
        }
    }

    /// <summary>
    /// A new recording of a list whose last run, ending at <paramref name="value"/> of <paramref name="fence"/>, goes
    /// on: nothing on the queue that runs it (its order), else that queue waits on the GPU. The waits of the last run's
    /// queue are released first, as before a CPU wait: the writer one of them needs may be queued behind this one.
    /// </summary>
    [HotPath]
    private static void After(XboxQueueObject* queue, nint fence, ulong value)
    {
        if (queue->Fence == fence)
        {
            Interlocked.Increment(ref _inOrder);

            return;
        }

        Interlocked.Increment(ref _queueWaits);
        MemoryWaits.ReleaseBehind(fence);
        nint inner = queue->Header.Inner;
        _ = ((delegate* unmanaged<nint, nint, ulong, int>)ComObject.Slot(inner, QueueWaitSlot))(inner, fence, value);
    }

    private static void WarnLongWait(nint list, long waited)
    {
        ShimContext.Runtime.Log.Warn($"[d3d12_xs] submitting list 0x{list:x} waited {waited} ms on the CPU for its previous run (thread {Environment.CurrentManagedThreadId})");
    }

    /// <summary>After a submission: signals the queue's fence and records the value in every wrapped list.</summary>
    [HotPath]
    public static void Submitted(XboxQueueObject* queue, nint* lists, uint count, XboxObjects objects)
    {
        if (queue->Fence == 0 && !CreateFence(queue, objects.DesktopDevice))
        {
            return;
        }

        ulong value = Interlocked.Increment(ref queue->Signaled);
        nint inner = queue->Header.Inner;
        _ = ((delegate* unmanaged<nint, nint, ulong, int>)ComObject.Slot(inner, QueueSignalSlot))(inner, queue->Fence, value);

        for (uint i = 0; i < count; i++)
        {
            nint list = lists[i];

            if (list != 0 && objects.Unwrap(list) != list)
            {
                *ListSubmission.Of(list) = new ListSubmission { Fence = queue->Fence, Value = value };
                GpuBreadcrumbs.Submitted(list, queue->Fence, value, (int)i);
            }
        }

        QueueProgress.Executed(queue, lists, count, value, objects);
        GpuTimings.Executed(queue->Number, inner, queue->Fence, value, new ReadOnlySpan<nint>(lists, (int)count));
    }

    private static ulong Completed(nint fence)
    {
        return ((delegate* unmanaged<nint, ulong>)ComObject.Slot(fence, GetCompletedValueSlot))(fence);
    }

    private static bool CreateFence(XboxQueueObject* queue, nint device)
    {
        if (device == 0)
        {
            return false;
        }

        Guid iid = FenceIid;
        nint fence = 0;
        int hr = ((delegate* unmanaged<nint, ulong, int, Guid*, nint*, int>)ComObject.Slot(device, CreateFenceSlot))(device, 0, 0, &iid, &fence);

        if (HResult.Failed(hr))
        {
            return false;
        }

        queue->Fence = fence;
        RetireWaits.Register(queue);
        ShimContext.Runtime.D3D12X.FrameThrottle.Follow(queue);

        return true;
    }
}
