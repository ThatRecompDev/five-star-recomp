// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// Command-list slots 73 and 74 (X-only), Wait32BitValueX and Wait64BitValueX: the queue does not go on until a value in
/// memory compares true (<see cref="MemoryComparison"/>).
/// <list type="bullet">
/// <item>73 (address, comparison, dword, mask, flags) (*inferred* from XCL's notes; no call site identified).</item>
/// <item>74 (address, comparison, qword, flags): the game passes GREATER_EQUAL and flags 1 (0x142484b0c); the whole
/// 64 bits are compared. Its callers: a frame number at a record's +0x50 (0x144e39e60) and the progress counter
/// 0x146891a00 at the cell 0x1475e75d0 (0x14031a6f0), which the CPU itself writes there at 0x14044b6c3.</item>
/// </list>
/// The desktop has no memory wait. Each list records its waits (<see cref="ListMemoryWaits"/>); at submission a list
/// whose waits do not all hold yet is preceded by a desktop queue Wait on a fence of this class, and a watcher thread
/// signals that fence once memory holds, or after <see cref="TimeoutMilliseconds"/> (logged). Waits are released in
/// submission order. The whole list waits, not only the commands after its wait.
/// </summary>
internal static unsafe class MemoryWaits
{
    public const int Wait32Slot = 73;
    public const int Wait64Slot = 74;

    /// <summary>A wait that has not held by then is released: a missing write must not hold a queue for ever.</summary>
    public const long TimeoutMilliseconds = 2000;

    /// <summary>ID3D12CommandQueue::Wait; ID3D12Fence::Signal; ID3D12Device::CreateFence (d3d12.h).</summary>
    private const int QueueWaitSlot = 15;
    private const int FenceSignalSlot = 10;
    private const int CreateFenceSlot = 36;

    private const int DwordBytes = 4;
    private const int QwordBytes = 8;
    private const long DetailedEvents = 16;
    private const int YieldPolls = 2000;
    private const int IdleMilliseconds = 100;

    private static readonly Guid FenceIid = new("0a753dcf-c4d8-4b91-adf6-be5a60d95a76");
    private static readonly object Gate = new();
    private static readonly Queue<PendingWait> Pending = new();
    private static readonly AutoResetEvent Wake = new(false);
    private static nint _fence;
    private static ulong _tickets;
    private static Thread? _watcher;
    private static LoaderLog? _log;
    private static long _recorded;
    private static long _held;
    private static long _deferred;
    private static long _timedOut;
    private static long _dropped;

    /// <summary>Waits recorded, met at submission, waited for on the GPU, released by the timeout, and not kept.</summary>
    public static (long Recorded, long Held, long Deferred, long TimedOut, long Dropped) Counts =>
        (Interlocked.Read(ref _recorded), Interlocked.Read(ref _held), Interlocked.Read(ref _deferred), Interlocked.Read(ref _timedOut), Interlocked.Read(ref _dropped));

    /// <summary>Binds the slots for a new device: its waits start over on a fence of that device.</summary>
    public static void Bind(XboxObjects objects, LoaderLog log)
    {
        lock (Gate)
        {
            _log = log;
            _fence = 0;
            _tickets = 0;
            Pending.Clear();
        }

        delegate* unmanaged<XboxObject*, ulong, uint, uint, uint, uint, int> wait32 = &Wait32;
        delegate* unmanaged<XboxObject*, ulong, uint, ulong, uint, int> wait64 = &Wait64;
        objects.SetHandler(XboxInterfaceKind.CommandList, Wait32Slot, (nint)wait32);
        objects.SetHandler(XboxInterfaceKind.CommandList, Wait64Slot, (nint)wait64);
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static int Wait32(XboxObject* self, ulong address, uint comparison, uint value, uint mask, uint flags)
    {
        Record(self, address, DwordBytes, (int)comparison, value, mask);

        return HResult.Ok;
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static int Wait64(XboxObject* self, ulong address, uint comparison, ulong value, uint flags)
    {
        RetireWaits.Recorded(self, address, value);
        Record(self, address, QwordBytes, (int)comparison, value, ulong.MaxValue);

        return HResult.Ok;
    }

    [HotPath]
    private static void Record(XboxObject* list, ulong address, int bytes, int comparison, ulong value, ulong mask)
    {
        Interlocked.Increment(ref _recorded);

        if (!MemoryComparison.Blocks(comparison) || !ListMemoryWaits.Of(list)->Record(address, bytes, comparison, value, mask))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Whether <paramref name="list"/> (a wrapper) recorded waits.</summary>
    [HotPath]
    public static bool HasWaits(nint list)
    {
        return ListMemoryWaits.Of((XboxObject*)list)->Count != 0;
    }

    /// <summary>
    /// Before <paramref name="queue"/> (desktop) runs <paramref name="list"/> (a wrapper with waits): returns at once; when
    /// a wait does not hold yet, the queue first waits on the GPU until the watcher releases it.
    /// </summary>
    [HotPath]
    public static void Before(nint queue, nint list)
    {
        var waits = ListMemoryWaits.Of((XboxObject*)list);
        int open = 0;
        var pending = stackalloc PendingWait[ListMemoryWaits.Capacity];

        for (int i = 0; i < waits->Count; i++)
        {
            var wait = new PendingWait(waits->Address[i], waits->Bytes[i], waits->Comparison[i], waits->Value[i], waits->Mask[i]);

            if (!wait.Resolve())
            {
                Interlocked.Increment(ref _dropped);
            }
            else if (wait.Met())
            {
                Interlocked.Increment(ref _held);
            }
            else
            {
                pending[open++] = wait;
            }
        }

        if (open != 0)
        {
            Defer(queue, pending, open);
        }
    }

    private static void Defer(nint queue, PendingWait* waits, int count)
    {
        ulong ticket;

        lock (Gate)
        {
            if (_fence == 0 && !CreateFence())
            {
                return;
            }

            ticket = ++_tickets;
            long deadline = Environment.TickCount64 + TimeoutMilliseconds;

            for (int i = 0; i < count; i++)
            {
                Pending.Enqueue(waits[i] with { Ticket = ticket, Deadline = deadline });
            }

            _ = ((delegate* unmanaged<nint, nint, ulong, int>)ComObject.Slot(queue, QueueWaitSlot))(queue, _fence, ticket);
        }

        long deferred = Interlocked.Add(ref _deferred, count);

        if (deferred <= DetailedEvents)
        {
            var first = waits[0];
            _log?.Info($"  [memory wait] queue waits for 0x{first.Address:x} (op {first.Comparison}) to reach 0x{first.Value:x}; it holds 0x{first.Current():x}");
        }

        Wake.Set();
    }

    private static bool CreateFence()
    {
        nint device = ShimContext.Runtime.D3D12X.Objects.DesktopDevice;

        if (device == 0)
        {
            return false;
        }

        Guid iid = FenceIid;
        nint fence = 0;
        int hr = ((delegate* unmanaged<nint, ulong, int, Guid*, nint*, int>)ComObject.Slot(device, CreateFenceSlot))(device, 0, 0, &iid, &fence);

        if (HResult.Failed(hr))
        {
            _log?.Warn($"[memory wait] no fence: {HResult.Format(hr)}; waits are not kept");

            return false;
        }

        _fence = fence;

        if (_watcher is null)
        {
            _watcher = new Thread(Watch) { IsBackground = true, Name = "fivestar memory waits" };
            _watcher.Start();
        }

        return true;
    }

    /// <summary>The watcher: releases the oldest wait once it holds or times out, then signals its ticket when its group is done.</summary>
    private static void Watch()
    {
        int polls = 0;

        while (true)
        {
            PendingWait front;

            lock (Gate)
            {
                if (!Pending.TryPeek(out front))
                {
                    front = default;
                }
            }

            if (front.Ticket == 0)
            {
                Wake.WaitOne(IdleMilliseconds);
                polls = 0;

                continue;
            }

            bool met = front.Met();

            if (!met && Environment.TickCount64 < front.Deadline)
            {
                Pause(ref polls);

                continue;
            }

            if (!met)
            {
                TimedOut(front);
            }

            Release();
            polls = 0;
        }
    }

    private static void Pause(ref int polls)
    {
        if (++polls < YieldPolls)
        {
            Thread.Yield();
        }
        else
        {
            Thread.Sleep(1);
        }
    }

    /// <summary>Takes the oldest wait off; when no wait of its ticket is left, signals the ticket.</summary>
    private static void Release()
    {
        lock (Gate)
        {
            var done = Pending.Dequeue();

            if (!Pending.TryPeek(out var next) || next.Ticket != done.Ticket)
            {
                _ = ((delegate* unmanaged<nint, ulong, int>)ComObject.Slot(_fence, FenceSignalSlot))(_fence, done.Ticket);
            }
        }
    }

    private static void TimedOut(in PendingWait wait)
    {
        long count = Interlocked.Increment(ref _timedOut);

        if (count <= DetailedEvents || (count & (count - 1)) == 0)
        {
            _log?.Warn($"[memory wait] 0x{wait.Address:x} (op {wait.Comparison}) did not reach 0x{wait.Value:x} in {TimeoutMilliseconds} ms (it holds 0x{wait.Current():x}): released; {count} so far");
        }
    }

    /// <summary>A wait to watch: the console address, its CPU address, the comparison and when it is released.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private record struct PendingWait(ulong Address, int Bytes, int Comparison, ulong Value, ulong Mask)
    {
        public nint Cpu;
        public ulong Ticket;
        public long Deadline;

        /// <summary>Finds where the CPU reads the address; false when nothing readable is there.</summary>
        public bool Resolve()
        {
            Cpu = ShimContext.Runtime.XMemPhysical.CpuAddress((nint)Address);
            MemoryBasicInformation info;

            if (Kernel32.VirtualQueryEx(Kernel32.GetCurrentProcess(), Cpu, &info, (nuint)sizeof(MemoryBasicInformation)) == 0 || info.State != Win32Constants.MemCommit || (info.Protect & Win32Constants.PageNoAccess) != 0)
            {
                return false;
            }

            return true;
        }

        public readonly ulong Current()
        {
            if (Bytes == DwordBytes)
            {
                return Volatile.Read(ref *(uint*)Cpu);
            }

            return Volatile.Read(ref *(ulong*)Cpu);
        }

        public readonly bool Met()
        {
            return MemoryComparison.Holds(Comparison, Current(), Value, Mask);
        }
    }
}
