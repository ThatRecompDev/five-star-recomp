// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Codegen;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// One look at another thread that never holds it while managed code runs: a generated routine suspends the thread,
/// reads its context, copies its stack from rsp up to the stack base and resumes it, all in native code. Suspending a
/// thread and then calling back into the runtime deadlocks the process when the thread was in managed code: a garbage
/// collection that starts meanwhile waits for that thread, and the caller's return from any P/Invoke waits for the
/// collection, so the resume never comes (a game run froze for good with the sampler in GetThreadContext and a game
/// thread suspended inside a shim). The caller is in preemptive mode for the whole routine, so a collection never
/// waits for it, and the suspended thread runs again before the routine returns. The copy is skipped when rsp lies below
/// the stack's committed limit (a stack the game switched to on its own, a fiber's): a copy from there up to the base
/// could reach unmapped memory and fault with the thread suspended. The routine runs on a worker thread and the caller
/// waits <see cref="TimeoutMilliseconds"/> at most: a thread whose suspend or context read does not come back is skipped
/// and reported (<see cref="Stuck"/>), its worker and buffers abandoned, so the sampler and the hang watch go on (w83's
/// sampler stopped for good inside a capture after its first sample).
/// </summary>
internal static unsafe class ThreadSnapshot
{
    /// <summary>The routine's argument block: the thread handle, the CONTEXT, the copy and its capacity, the stack base, then the bytes copied, the status and the stack's committed limit.</summary>
    private const int ThreadField = 0x00;
    private const int ContextField = 0x08;
    private const int CopyField = 0x10;
    private const int CapacityField = 0x18;
    private const int BaseField = 0x20;
    private const int CopiedField = 0x28;
    private const int StatusField = 0x30;
    private const int LimitField = 0x38;
    private const int ArgumentBytes = 0x40;

    /// <summary>Where the routine's movabs immediates take SuspendThread, GetThreadContext, memcpy and ResumeThread.</summary>
    private const int SuspendPatch = 0x1C;
    private const int ContextPatch = 0x34;
    private const int CopyPatch = 0x70;
    private const int ResumePatch = 0x8F;

    /// <summary>The status of a snapshot taken: context read, stack copied (possibly empty), thread resumed.</summary>
    private const int Taken = 2;

    /// <summary>THREADINFOCLASS ThreadBasicInformation, sizeof(THREAD_BASIC_INFORMATION) and its TebBaseAddress.</summary>
    private const int BasicInformationClass = 0;
    private const int BasicInformationSize = 48;
    private const int TebBaseAddressOffset = 8;

    /// <summary>CONTEXT bytes the caller passes (AMD64, aligned by the caller).</summary>
    private const int ContextBytes = 1232;
    private const int ContextAlignment = 16;

    /// <summary>DUPLICATE_SAME_ACCESS (winnt.h).</summary>
    private const uint DuplicateSameAccess = 2;

    /// <summary>The longest a snapshot may take: suspend, context and copy take microseconds.</summary>
    public const int TimeoutMilliseconds = 250;

    /// <summary>
    /// The routine (llvm-mc of: push rbx; sub rsp, 20h; rbx = args; SuspendThread(thread), done on -1;
    /// GetThreadContext(thread, context), status 1 on failure; when context.Rsp &gt;= limit: r8 = min(base - context.Rsp,
    /// capacity), memcpy(copy, context.Rsp, r8) when the base is above rsp; status 2; ResumeThread(thread); return).
    /// </summary>
    private static readonly byte[] Routine =
    [
        0x53, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x89, 0xCB, 0x48, 0xC7, 0x43, 0x28, 0x00, 0x00, 0x00, 0x00, 0xC7, 0x43, 0x30,
        0x00, 0x00, 0x00, 0x00, 0x48, 0x8B, 0x0B, 0x48, 0xB8, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0xFF, 0xD0,
        0x83, 0xF8, 0xFF, 0x74, 0x6E, 0x48, 0x8B, 0x0B, 0x48, 0x8B, 0x53, 0x08, 0x48, 0xB8, 0x22, 0x22, 0x22, 0x22, 0x22,
        0x22, 0x22, 0x22, 0xFF, 0xD0, 0x85, 0xC0, 0x74, 0x41, 0x48, 0x8B, 0x43, 0x08, 0x48, 0x8B, 0x90, 0x98, 0x00, 0x00,
        0x00, 0x48, 0x3B, 0x53, 0x38, 0x72, 0x27, 0x4C, 0x8B, 0x43, 0x20, 0x49, 0x29, 0xD0, 0x76, 0x1E, 0x4C, 0x3B, 0x43,
        0x18, 0x76, 0x04, 0x4C, 0x8B, 0x43, 0x18, 0x4C, 0x89, 0x43, 0x28, 0x48, 0x8B, 0x4B, 0x10, 0x48, 0xB8, 0x33, 0x33,
        0x33, 0x33, 0x33, 0x33, 0x33, 0x33, 0xFF, 0xD0, 0xC7, 0x43, 0x30, 0x02, 0x00, 0x00, 0x00, 0xEB, 0x07, 0xC7, 0x43,
        0x30, 0x01, 0x00, 0x00, 0x00, 0x48, 0x8B, 0x0B, 0x48, 0xB8, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0xFF,
        0xD0, 0x48, 0x83, 0xC4, 0x20, 0x5B, 0xC3,
    ];

    private static readonly Lazy<nint> Code = new(Emit);
    private static readonly object Gate = new();
    private static Worker? _worker;

    /// <summary>Reports a thread whose snapshot did not come back in time (the thread id); null reports nothing.</summary>
    public static Action<uint>? Stuck { get; set; }

    /// <summary>The routine run instead of the generated one (tests: one that never comes back); 0 for the generated one.</summary>
    internal static nint Override { get; set; }

    /// <summary>
    /// Takes a snapshot of <paramref name="thread"/> (a handle with suspend, get-context and query rights): fills
    /// <paramref name="context"/> (aligned, its ContextFlags set) and copies its stack into <paramref name="copy"/>;
    /// the bytes copied, from the context's rsp up, or -1 when the thread could not be read or did not answer in
    /// <see cref="TimeoutMilliseconds"/>. The thread runs again when this returns (unless its snapshot is stuck), so
    /// the copy, not the live stack, is what a walk reads.
    /// </summary>
    public static long Take(nint thread, byte* context, byte* copy, long capacity)
    {
        var (stackBase, stackLimit) = Stack(thread);

        if (stackBase == 0)
        {
            return -1;
        }

        // Buffers of the worker's own: abandoned with it when the snapshot does not come back, never the caller's.
        byte* block = (byte*)NativeMemory.AlignedAlloc((nuint)(ArgumentBytes + ContextBytes + capacity), ContextAlignment);
        byte* arguments = block;
        byte* ownContext = block + ArgumentBytes;
        Buffer.MemoryCopy(context, ownContext, ContextBytes, ContextBytes);

        // A handle of the worker's own too: the caller closes its handle when this returns, a stuck worker resumes later.
        nint own = 0;

        if (Kernel32.DuplicateHandle(Kernel32.GetCurrentProcess(), thread, Kernel32.GetCurrentProcess(), &own, 0, 0, DuplicateSameAccess) == 0)
        {
            NativeMemory.AlignedFree(block);

            return -1;
        }

        *(nint*)(arguments + ThreadField) = own;
        *(byte**)(arguments + ContextField) = ownContext;
        *(byte**)(arguments + CopyField) = ownContext + ContextBytes;
        *(long*)(arguments + CapacityField) = capacity;

        *(ulong*)(arguments + BaseField) = stackBase;
        *(ulong*)(arguments + LimitField) = stackLimit;

        if (!Run(arguments, Kernel32.GetThreadId(thread)))
        {
            return -1;
        }

        long copied = -1;

        if (*(int*)(arguments + StatusField) == Taken)
        {
            copied = *(long*)(arguments + CopiedField);
            Buffer.MemoryCopy(ownContext, context, ContextBytes, ContextBytes);
            Buffer.MemoryCopy(ownContext + ContextBytes, copy, capacity, copied);
        }

        _ = Kernel32.CloseHandle(own);
        NativeMemory.AlignedFree(block);

        return copied;
    }

    /// <summary>Runs the routine on the worker; false (the worker and the block abandoned, the thread reported) when it does not come back in time.</summary>
    private static bool Run(byte* arguments, uint threadId)
    {
        lock (Gate)
        {
            var worker = _worker ??= new Worker();
            nint routine = Override;

            if (routine == 0)
            {
                routine = Code.Value;
            }

            if (worker.Run(routine, arguments, TimeoutMilliseconds))
            {
                return true;
            }

            _worker = null;
        }

        Stuck?.Invoke(threadId);

        return false;
    }

    /// <summary>The thread's NT_TIB.StackBase and StackLimit, (0, 0) when they cannot be read.</summary>
    private static (ulong Base, ulong Limit) Stack(nint thread)
    {
        byte* information = stackalloc byte[BasicInformationSize];

        if (Ntdll.NtQueryInformationThread(thread, BasicInformationClass, information, BasicInformationSize, null) != 0)
        {
            return (0, 0);
        }

        byte* teb = *(byte**)(information + TebBaseAddressOffset);

        if (teb == null)
        {
            return (0, 0);
        }

        return (*(ulong*)(teb + ThreadEnvironmentBlock.StackBase), *(ulong*)(teb + ThreadEnvironmentBlock.StackLimit));
    }

    private static nint Emit()
    {
        nint kernel32 = NativeLibrary.Load("kernel32.dll");
        byte[] code = [.. Routine];
        Patch(code, SuspendPatch, NativeLibrary.GetExport(kernel32, "SuspendThread"));
        Patch(code, ContextPatch, NativeLibrary.GetExport(kernel32, "GetThreadContext"));
        Patch(code, CopyPatch, NativeLibrary.GetExport(NativeLibrary.Load("ntdll.dll"), "memcpy"));
        Patch(code, ResumePatch, NativeLibrary.GetExport(kernel32, "ResumeThread"));

        var heap = new CodeHeap();
        nint address = heap.Write(code);
        heap.Seal();

        return address;
    }

    private static void Patch(byte[] code, int offset, nint function)
    {
        BitConverter.TryWriteBytes(code.AsSpan(offset, sizeof(ulong)), (ulong)function);
    }

    /// <summary>A thread that runs the routine for one request at a time; abandoned when a request does not come back.</summary>
    private sealed class Worker
    {
        private readonly object _sync = new();
        private nint _routine;
        private nint _arguments;
        private bool _pending;
        private bool _finished;

        public Worker()
        {
            new Thread(Loop) { IsBackground = true, Name = "fivestar thread snapshot" }.Start();
        }

        /// <summary>Runs <paramref name="routine"/> on <paramref name="arguments"/>; false when it did not come back in <paramref name="milliseconds"/>.</summary>
        public bool Run(nint routine, byte* arguments, int milliseconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();

            lock (_sync)
            {
                _routine = routine;
                _arguments = (nint)arguments;
                _finished = false;
                _pending = true;
                Monitor.PulseAll(_sync);

                while (!_finished)
                {
                    int left = milliseconds - (int)clock.ElapsedMilliseconds;

                    if (left <= 0 || !Monitor.Wait(_sync, left))
                    {
                        return _finished;
                    }
                }

                return true;
            }
        }

        private void Loop()
        {
            while (true)
            {
                nint arguments;
                nint routine;

                lock (_sync)
                {
                    while (!_pending)
                    {
                        Monitor.Wait(_sync);
                    }

                    _pending = false;
                    arguments = _arguments;
                    routine = _routine;
                }

                ((delegate* unmanaged<byte*, void>)routine)((byte*)arguments);

                lock (_sync)
                {
                    _finished = true;
                    Monitor.PulseAll(_sync);
                }
            }
        }
    }
}
