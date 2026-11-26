// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Shims.D3D12X;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// A list still running from its last submission: the same recording waits on the CPU before it runs again, a new
/// recording does not (its queue's order, or a GPU wait on another queue).
/// </summary>
public sealed unsafe class D3D12XListSubmissionTests
{
    private const int Slots = 16;
    private const int CompletedSlot = 8;
    private const int EventSlot = 9;
    private const int WaitSlot = 15;
    private const ulong LastRun = 5;

    private static int _cpuWaits;
    private static int _gpuWaits;
    private static ulong _gpuWaitValue;

    [Fact]
    public void A_new_recording_on_the_same_queue_runs_in_its_order_without_any_wait()
    {
        Run(resetSince: 1, sameQueue: true);

        Assert.Equal(0, _cpuWaits);
        Assert.Equal(0, _gpuWaits);
    }

    [Fact]
    public void A_new_recording_on_another_queue_waits_for_the_last_run_on_the_gpu()
    {
        Run(resetSince: 1, sameQueue: false);

        Assert.Equal(0, _cpuWaits);
        Assert.Equal(1, _gpuWaits);
        Assert.Equal(LastRun, _gpuWaitValue);
    }

    [Fact]
    public void The_same_recording_submitted_again_waits_on_the_cpu()
    {
        Run(resetSince: 0, sameQueue: true);

        Assert.Equal(1, _cpuWaits);
        Assert.Equal(0, _gpuWaits);
    }

    private static void Run(int resetSince, bool sameQueue)
    {
        _cpuWaits = 0;
        _gpuWaits = 0;
        _gpuWaitValue = 0;

        var vtable = (nint*)NativeMemory.AllocZeroed(Slots, (nuint)sizeof(nint));
        var fence = (nint*)NativeMemory.AllocZeroed(1, (nuint)sizeof(nint));
        var other = (nint*)NativeMemory.AllocZeroed(1, (nuint)sizeof(nint));
        var desktopQueue = (nint*)NativeMemory.AllocZeroed(1, (nuint)sizeof(nint));
        var queue = (XboxQueueObject*)NativeMemory.AllocZeroed((nuint)sizeof(XboxQueueObject));
        var list = (XboxObject*)NativeMemory.AllocZeroed(XboxInterfaces.CommandListObjectSize);

        try
        {
            vtable[CompletedSlot] = (nint)(delegate* unmanaged<nint, ulong>)&Completed;
            vtable[EventSlot] = (nint)(delegate* unmanaged<nint, ulong, nint, int>)&SetEvent;
            vtable[WaitSlot] = (nint)(delegate* unmanaged<nint, nint, ulong, int>)&Wait;
            *fence = (nint)vtable;
            *other = (nint)vtable;
            *desktopQueue = (nint)vtable;

            queue->Header.Inner = (nint)desktopQueue;
            queue->Fence = (nint)other;

            if (sameQueue)
            {
                queue->Fence = (nint)fence;
            }

            *ListSubmission.Of((nint)list) = new ListSubmission { Fence = (nint)fence, Value = LastRun, ResetSince = resetSince };

            ListSubmissions.WaitIfRunning((nint)list, queue);
        }
        finally
        {
            NativeMemory.Free(list);
            NativeMemory.Free(queue);
            NativeMemory.Free(desktopQueue);
            NativeMemory.Free(other);
            NativeMemory.Free(fence);
            NativeMemory.Free(vtable);
        }
    }

    /// <summary>ID3D12Fence::GetCompletedValue: the last run has not finished.</summary>
    [UnmanagedCallersOnly]
    private static ulong Completed(nint self)
    {
        return 0;
    }

    /// <summary>ID3D12Fence::SetEventOnCompletion with a null event: the CPU wait.</summary>
    [UnmanagedCallersOnly]
    private static int SetEvent(nint self, ulong value, nint handle)
    {
        _cpuWaits++;

        return 0;
    }

    /// <summary>ID3D12CommandQueue::Wait: the GPU wait.</summary>
    [UnmanagedCallersOnly]
    private static int Wait(nint self, nint fence, ulong value)
    {
        _gpuWaits++;
        _gpuWaitValue = value;

        return 0;
    }
}
