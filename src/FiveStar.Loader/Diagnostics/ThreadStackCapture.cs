// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// Captures another thread's return addresses: a <see cref="ThreadSnapshot"/> (context and a copy of its stack, the
/// thread resumed at once), then an unwind through .pdata over the copy. The thread is never held while managed code
/// runs: holding it across our own P/Invokes deadlocked the process when it was suspended in managed code and a
/// garbage collection started. The unwind reads the copy: rsp, and rbp wherever it points into the copied range, are
/// moved into it before each step. The walk stops at the first address outside the given modules, so JIT code, whose
/// unwind data the runtime answers through a callback, is never unwound.
/// </summary>
internal static unsafe class ThreadStackCapture
{
    // winnt.h: THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION.
    private const uint ThreadAccess = 0x0002 | 0x0008 | 0x0040;

    // CONTEXT (winnt.h, AMD64): size, alignment, ContextFlags, Rsp, Rbp, Rsi, Rdi and Rip; CONTEXT_CONTROL | CONTEXT_INTEGER.
    private const int ContextSize = 1232;
    private const int ContextAlignment = 16;
    private const int ContextFlagsOffset = 0x30;
    private const int RspOffset = 0x98;
    private const int RbpOffset = 0xA0;
    private const int RsiOffset = 0xA8;
    private const int RdiOffset = 0xB0;
    private const int RipOffset = 0xF8;

    /// <summary>Frames a register probe walks at most.</summary>
    private const int MaxProbeFrames = 64;
    private const uint ContextControlInteger = 0x100003;

    /// <summary>UNW_FLAG_NHANDLER: unwind without calling handlers.</summary>
    private const uint NoHandler = 0;

    /// <summary>Stack bytes copied at most, from rsp up.</summary>
    private const long CopyCapacity = 1 << 20;

    /// <summary>Stack bytes scanned for the game's caller above the frame a walk ended at: our managed frames sit below it.</summary>
    private const ulong ScannedBytes = 64 * 1024;

    private static readonly object Gate = new();
    private static byte* _copy;

    /// <summary>Fills <paramref name="pcs"/> with the thread's frames, innermost first; returns the count (0: not read).</summary>
    public static int Capture(uint threadId, ReadOnlySpan<CodeRange> modules, Span<ulong> pcs)
    {
        byte* raw = stackalloc byte[ContextSize + ContextAlignment];
        byte* context = Aligned(raw);

        lock (Gate)
        {
            var stack = Snapshot(threadId, context);

            if (stack.Bytes < 0)
            {
                return 0;
            }

            return Walk(context, stack, modules, pcs);
        }
    }

    /// <summary>
    /// <see cref="Capture(uint, ReadOnlySpan{CodeRange}, Span{ulong})"/> with at most <paramref name="capacity"/> stack
    /// bytes copied, and, when <paramref name="scanAbove"/> holds for the frame the walk ended at (JIT code, which it
    /// cannot unwind), the first return address into <paramref name="caller"/> above that frame in the copy: the
    /// game's call into the code the thread is in (<see cref="ReturnAddresses"/>'s test); 0 when there is none.
    /// </summary>
    public static int Capture(uint threadId, ReadOnlySpan<CodeRange> modules, Span<ulong> pcs, long capacity, Func<ulong, bool> scanAbove, Func<ulong, bool> caller, out ulong site)
    {
        byte* raw = stackalloc byte[ContextSize + ContextAlignment];
        byte* context = Aligned(raw);
        site = 0;

        lock (Gate)
        {
            var stack = Snapshot(threadId, context, Math.Min(capacity, CopyCapacity));

            if (stack.Bytes < 0)
            {
                return 0;
            }

            int count = Walk(context, stack, modules, pcs);

            if (count > 0 && scanAbove(pcs[count - 1]))
            {
                site = FirstCaller(stack, *(ulong*)(context + RspOffset), caller);
            }

            return count;
        }
    }

    /// <summary>The first value from <paramref name="from"/> (an address in the copy) up that passes <paramref name="caller"/>, or 0.</summary>
    private static ulong FirstCaller(StackCopy stack, ulong from, Func<ulong, bool> caller)
    {
        if (!stack.Holds(from))
        {
            return 0;
        }

        ulong end = Math.Min((ulong)stack.Copy + (ulong)stack.Bytes, from + ScannedBytes);

        for (ulong slot = from; slot + sizeof(ulong) <= end; slot += sizeof(ulong))
        {
            ulong value = *(ulong*)slot;

            if (caller(value))
            {
                return value;
            }
        }

        return 0;
    }

    /// <summary>
    /// The nonvolatile RSI and RDI of the thread's frame in <paramref name="function"/>, as the unwind restores them
    /// (a wait's arguments the function keeps there); false when no frame of the thread is in it.
    /// </summary>
    public static bool FrameRegisters(uint threadId, ReadOnlySpan<CodeRange> modules, CodeRange function, out ulong rsi, out ulong rdi)
    {
        rsi = 0;
        rdi = 0;
        byte* raw = stackalloc byte[ContextSize + ContextAlignment];
        byte* context = Aligned(raw);
        Span<ulong> pcs = stackalloc ulong[MaxProbeFrames];

        lock (Gate)
        {
            var stack = Snapshot(threadId, context);

            if (stack.Bytes < 0)
            {
                return false;
            }

            _ = Walk(context, stack, modules, pcs, function);
        }

        rsi = *(ulong*)(context + RsiOffset);
        rdi = *(ulong*)(context + RdiOffset);

        return function.Contains(*(ulong*)(context + RipOffset));
    }

    private static byte* Aligned(byte* raw)
    {
        byte* context = (byte*)(((ulong)raw + ContextAlignment - 1) & ~(ulong)(ContextAlignment - 1));
        *(uint*)(context + ContextFlagsOffset) = ContextControlInteger;

        return context;
    }

    /// <summary>The thread's context and stack copy (<see cref="StackCopy.Bytes"/> -1 when it could not be read); called under <see cref="Gate"/>.</summary>
    private static StackCopy Snapshot(uint threadId, byte* context, long capacity = CopyCapacity)
    {
        // The calling thread is never read: suspending itself, it would never resume and would keep the gate (a hang
        // watch walking every thread of the process did, and every later sample and report waited on it).
        if (threadId == Kernel32.GetCurrentThreadId())
        {
            return new StackCopy(0, 0, -1);
        }

        nint thread = Kernel32.OpenThread(ThreadAccess, 0, threadId);

        if (thread == 0)
        {
            return new StackCopy(0, 0, -1);
        }

        if (_copy == null)
        {
            _copy = (byte*)NativeMemory.Alloc((nuint)CopyCapacity);
        }

        long bytes = ThreadSnapshot.Take(thread, context, _copy, capacity);
        _ = Kernel32.CloseHandle(thread);
        var stack = new StackCopy(*(ulong*)(context + RspOffset), (nint)_copy, bytes);

        if (bytes >= 0)
        {
            *(ulong*)(context + RspOffset) = (ulong)_copy;
            stack.MoveFramePointer(context + RbpOffset);
        }

        return stack;
    }

    private static int Walk(byte* context, StackCopy stack, ReadOnlySpan<CodeRange> modules, Span<ulong> pcs, CodeRange stopAt = default)
    {
        int count = 0;

        while (count < pcs.Length)
        {
            ulong pc = *(ulong*)(context + RipOffset);
            ulong rsp = *(ulong*)(context + RspOffset);

            if (pc == 0 || stopAt.Contains(pc))
            {
                break;
            }

            pcs[count++] = pc;

            if (!InModules(pc, modules) || !stack.Holds(rsp))
            {
                break;
            }

            ulong imageBase = 0;
            nint function = Ntdll.RtlLookupFunctionEntry(pc, &imageBase, 0);

            if (function == 0 && count > 1)
            {
                break;
            }

            Unwind(context, pc, imageBase, function);
            stack.MoveFramePointer(context + RbpOffset);

            if (*(ulong*)(context + RspOffset) <= rsp)
            {
                break;
            }
        }

        return count;
    }

    private static bool InModules(ulong pc, ReadOnlySpan<CodeRange> modules)
    {
        foreach (var range in modules)
        {
            if (range.Contains(pc))
            {
                return true;
            }
        }

        return false;
    }

    private static void Unwind(byte* context, ulong pc, ulong imageBase, nint function)
    {
        if (function == 0)
        {
            // A leaf function: the return address is at [rsp].
            ulong rsp = *(ulong*)(context + RspOffset);
            *(ulong*)(context + RipOffset) = *(ulong*)rsp;
            *(ulong*)(context + RspOffset) = rsp + sizeof(ulong);

            return;
        }

        nint handlerData;
        ulong establisherFrame;
        Ntdll.RtlVirtualUnwind(NoHandler, imageBase, pc, function, context, &handlerData, &establisherFrame, 0);
    }
}
