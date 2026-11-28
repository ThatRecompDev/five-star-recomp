// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// The bottom-of-pipe writes (slots 71 and 72, <see cref="BottomOfPipeWrites"/>) of a recording whose desktop Close
/// failed: an invalid call the shim recorded, which the console's Close would not refuse. The desktop list never runs,
/// so those values (what other queues' memory waits and the game's CPU wait for) never landed, and every wait for them
/// held until its timeout (issue #64: 10-16 s frames). When the game submits such a list, the submission leaves the
/// failed desktop list out (<see cref="XboxQueueMethods"/>), and once the queue's fence has passed the submission the
/// CPU writes each address the list is still the last writer of, among its last
/// <see cref="ListMemoryWaits.WriteCapacity"/> writes, in the order the list recorded them.
/// </summary>
internal static unsafe class LostListWrites
{
    /// <summary>ID3D12Fence::GetCompletedValue and ::SetEventOnCompletion (d3d12.h).</summary>
    private const int GetCompletedValueSlot = 8;
    private const int SetEventOnCompletionSlot = 9;

    private static long _landed;

    /// <summary>Values written from the CPU for lists that failed Close.</summary>
    public static long Landed => Interlocked.Read(ref _landed);

    /// <summary>Whether a desktop list of <paramref name="list"/>'s recording (a wrapper) failed Close.</summary>
    [HotPath]
    public static bool Has(nint list)
    {
        return ListMemoryWaits.Of((XboxObject*)list)->WritesLost != 0;
    }

    /// <summary>A desktop Close of <paramref name="list"/>'s recording failed; <paramref name="last"/>: the list it records on now, which never runs.</summary>
    public static void CloseFailed(XboxObject* list, bool last)
    {
        var waits = ListMemoryWaits.Of(list);
        waits->WritesLost = 1;

        if (last)
        {
            waits->LastCloseFailed = 1;
        }
    }

    /// <summary>The writes of <paramref name="list"/> to land, oldest first: each address once, while the list is still the last that recorded a write there.</summary>
    public static List<(ulong Address, ulong Value, int Bytes)> Collect(XboxObject* list)
    {
        var waits = ListMemoryWaits.Of(list);
        int writes = waits->Writes;
        int oldest = Math.Max(0, writes - ListMemoryWaits.WriteCapacity);
        var seen = new HashSet<ulong>();
        var found = new List<(ulong Address, ulong Value, int Bytes)>();

        for (int i = writes - 1; i >= oldest; i--)
        {
            ulong address = waits->Written[i % ListMemoryWaits.WriteCapacity];

            if (seen.Add(address) && MemoryWaitWriters.LastWrite((nint)list, address, out ulong value, out int bytes))
            {
                found.Add((address, value, bytes));
            }
        }

        found.Reverse();

        return found;
    }

    /// <summary>After <paramref name="list"/> (a wrapper whose recording failed Close) was submitted to <paramref name="queue"/>: its writes land once the queue has run what came before.</summary>
    public static void Submitted(XboxQueueObject* queue, nint list)
    {
        var writes = Collect((XboxObject*)list);

        if (writes.Count == 0)
        {
            return;
        }

        ShimContext.Runtime.D3D12X.Warnings.Warn("close-lost", $"[d3d12_xs] a list whose Close failed was submitted to queue #{queue->Number}: its failed desktop list is left out, and its bottom-of-pipe writes ({writes.Count} here) land from the CPU once the queue gets there");
        var submission = ListSubmission.Of(list);
        nint fence = queue->Fence;

        if (fence == 0 || submission->Fence != fence || Completed(fence) >= submission->Value)
        {
            Land(writes);

            return;
        }

        var pending = new PendingLand(writes);
        int hr = ((delegate* unmanaged<nint, ulong, nint, int>)ComObject.Slot(fence, SetEventOnCompletionSlot))(fence, submission->Value, pending.Done.SafeWaitHandle.DangerousGetHandle());

        if (HResult.Failed(hr))
        {
            pending.Done.Dispose();
            Land(writes);

            return;
        }

        pending.Wait();
    }

    /// <summary>Writes each value at the CPU address of its game address, through the kernel: a page unmapped since is skipped instead of faulting.</summary>
    private static void Land(List<(ulong Address, ulong Value, int Bytes)> writes)
    {
        var physical = ShimContext.Runtime.XMemPhysical;

        foreach (var (address, value, bytes) in writes)
        {
            ulong copy = value;
            nuint written = 0;
            nint cpu = physical.CpuAddress((nint)address);

            if (Kernel32.WriteProcessMemory(Kernel32.GetCurrentProcess(), cpu, &copy, (nuint)bytes, &written) != 0 && written == (nuint)bytes)
            {
                Interlocked.Increment(ref _landed);
            }
        }
    }

    private static ulong Completed(nint fence)
    {
        return ((delegate* unmanaged<nint, ulong>)ComObject.Slot(fence, GetCompletedValueSlot))(fence);
    }

    /// <summary>Writes waiting for the fence's event on the thread pool.</summary>
    private sealed class PendingLand(List<(ulong Address, ulong Value, int Bytes)> writes)
    {
        private readonly object _gate = new();
        private RegisteredWaitHandle? _registration;
        private bool _landed;

        public ManualResetEvent Done { get; } = new(false);

        public void Wait()
        {
            var registration = ThreadPool.RegisterWaitForSingleObject(Done, (_, _) => Fire(), null, Timeout.Infinite, executeOnlyOnce: true);

            lock (_gate)
            {
                _registration = registration;

                if (_landed)
                {
                    Finish();
                }
            }
        }

        private void Fire()
        {
            try
            {
                Land(writes);
            }
            catch (Exception exception)
            {
                // A thread-pool callback must not throw (it ends the process); a runtime disposed meanwhile has no memory to write.
                ShimContext.Log?.Warn($"[d3d12_xs] writes of a list that failed Close not landed: {exception.Message}");
            }

            lock (_gate)
            {
                _landed = true;

                if (_registration is not null)
                {
                    Finish();
                }
            }
        }

        private void Finish()
        {
            _registration!.Unregister(null);
            Done.Dispose();
        }
    }
}
