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
/// waits for it, and the suspended thread runs again before the routine returns.
/// </summary>
internal static unsafe class ThreadSnapshot
{
    /// <summary>The routine's argument block: the thread handle, the CONTEXT, the copy and its capacity, the stack base, then the bytes copied and the status.</summary>
    private const int ThreadField = 0x00;
    private const int ContextField = 0x08;
    private const int CopyField = 0x10;
    private const int CapacityField = 0x18;
    private const int BaseField = 0x20;
    private const int CopiedField = 0x28;
    private const int StatusField = 0x30;
    private const int ArgumentBytes = 0x38;

    /// <summary>Where the routine's movabs immediates take SuspendThread, GetThreadContext, memcpy and ResumeThread.</summary>
    private const int SuspendPatch = 0x1C;
    private const int ContextPatch = 0x34;
    private const int CopyPatch = 0x6A;
    private const int ResumePatch = 0x89;

    /// <summary>The status of a snapshot taken: context read, stack copied (possibly empty), thread resumed.</summary>
    private const int Taken = 2;

    /// <summary>THREADINFOCLASS ThreadBasicInformation, sizeof(THREAD_BASIC_INFORMATION) and its TebBaseAddress.</summary>
    private const int BasicInformationClass = 0;
    private const int BasicInformationSize = 48;
    private const int TebBaseAddressOffset = 8;

    /// <summary>
    /// The routine (llvm-mc of: push rbx; sub rsp, 20h; rbx = args; SuspendThread(thread), done on -1;
    /// GetThreadContext(thread, context), status 1 on failure; r8 = min(base - context.Rsp, capacity), memcpy(copy,
    /// context.Rsp, r8) when the base is above rsp, status 2; ResumeThread(thread); return).
    /// </summary>
    private static readonly byte[] Routine =
    [
        0x53, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x89, 0xCB, 0x48, 0xC7, 0x43, 0x28, 0x00, 0x00, 0x00, 0x00, 0xC7, 0x43, 0x30,
        0x00, 0x00, 0x00, 0x00, 0x48, 0x8B, 0x0B, 0x48, 0xB8, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0xFF, 0xD0,
        0x83, 0xF8, 0xFF, 0x74, 0x68, 0x48, 0x8B, 0x0B, 0x48, 0x8B, 0x53, 0x08, 0x48, 0xB8, 0x22, 0x22, 0x22, 0x22, 0x22,
        0x22, 0x22, 0x22, 0xFF, 0xD0, 0x85, 0xC0, 0x74, 0x3B, 0x48, 0x8B, 0x43, 0x08, 0x48, 0x8B, 0x90, 0x98, 0x00, 0x00,
        0x00, 0x4C, 0x8B, 0x43, 0x20, 0x49, 0x29, 0xD0, 0x76, 0x1E, 0x4C, 0x3B, 0x43, 0x18, 0x76, 0x04, 0x4C, 0x8B, 0x43,
        0x18, 0x4C, 0x89, 0x43, 0x28, 0x48, 0x8B, 0x4B, 0x10, 0x48, 0xB8, 0x33, 0x33, 0x33, 0x33, 0x33, 0x33, 0x33, 0x33,
        0xFF, 0xD0, 0xC7, 0x43, 0x30, 0x02, 0x00, 0x00, 0x00, 0xEB, 0x07, 0xC7, 0x43, 0x30, 0x01, 0x00, 0x00, 0x00, 0x48,
        0x8B, 0x0B, 0x48, 0xB8, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0xFF, 0xD0, 0x48, 0x83, 0xC4, 0x20, 0x5B,
        0xC3,
    ];

    private static readonly Lazy<nint> Code = new(Emit);

    /// <summary>
    /// Takes a snapshot of <paramref name="thread"/> (a handle with suspend, get-context and query rights): fills
    /// <paramref name="context"/> (aligned, its ContextFlags set) and copies its stack into <paramref name="copy"/>;
    /// the bytes copied, from the context's rsp up, or -1 when the thread could not be read. The thread runs again
    /// when this returns, so the copy, not the live stack, is what a walk reads.
    /// </summary>
    public static long Take(nint thread, byte* context, byte* copy, long capacity)
    {
        ulong stackBase = StackBase(thread);

        if (stackBase == 0)
        {
            return -1;
        }

        byte* arguments = stackalloc byte[ArgumentBytes];
        *(nint*)(arguments + ThreadField) = thread;
        *(byte**)(arguments + ContextField) = context;
        *(byte**)(arguments + CopyField) = copy;

        *(long*)(arguments + CapacityField) = capacity;
        *(ulong*)(arguments + BaseField) = stackBase;
        ((delegate* unmanaged<byte*, void>)Code.Value)(arguments);

        if (*(int*)(arguments + StatusField) != Taken)
        {
            return -1;
        }

        return *(long*)(arguments + CopiedField);
    }

    /// <summary>The thread's NT_TIB.StackBase, 0 when it cannot be read.</summary>
    private static ulong StackBase(nint thread)
    {
        byte* information = stackalloc byte[BasicInformationSize];

        if (Ntdll.NtQueryInformationThread(thread, BasicInformationClass, information, BasicInformationSize, null) != 0)
        {
            return 0;
        }

        byte* teb = *(byte**)(information + TebBaseAddressOffset);

        if (teb == null)
        {
            return 0;
        }

        return *(ulong*)(teb + ThreadEnvironmentBlock.StackBase);
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
}
