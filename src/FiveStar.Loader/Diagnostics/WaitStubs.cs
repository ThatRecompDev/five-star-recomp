// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// The system-call stubs a blocked thread sits in, with their names. Any other innermost frame runs, in user mode or in
/// the kernel (VirtualQuery's walk, for one, is inside ntdll's ZwQueryVirtualMemory).
/// </summary>
internal static class WaitStubs
{
    /// <summary>A system-call stub: mov r10, rcx; mov eax, n; test; jne; syscall; ret; int 2e; ret.</summary>
    private const ulong StubBytes = 0x20;

    private static readonly (string Module, string[] Calls)[] WaitCalls =
    [
        ("ntdll.dll", ["NtWaitForSingleObject", "NtWaitForMultipleObjects", "NtWaitForAlertByThreadId", "NtDelayExecution", "NtRemoveIoCompletion", "NtRemoveIoCompletionEx", "NtWaitForWorkViaWorkerFactory", "NtSignalAndWaitForSingleObject", "NtWaitForKeyedEvent", "NtReplyWaitReceivePort", "NtAlpcSendWaitReceivePort"]),
        ("win32u.dll", ["NtUserMsgWaitForMultipleObjectsEx", "NtUserGetMessage", "NtUserWaitMessage", "NtGdiDdDDIWaitForSynchronizationObjectFromCpu", "NtGdiDdDDIWaitForVerticalBlankEvent2"]),
    ];

    /// <summary>Each stub's range and its export name.</summary>
    public static (CodeRange Range, string Name)[] Find()
    {
        var stubs = new List<(CodeRange, string)>();

        foreach (var (module, calls) in WaitCalls)
        {
            if (!NativeLibrary.TryLoad(module, out nint handle))
            {
                continue;
            }

            foreach (string call in calls)
            {
                if (NativeLibrary.TryGetExport(handle, call, out nint stub))
                {
                    stubs.Add((new CodeRange((ulong)stub, (ulong)stub + StubBytes), call));
                }
            }
        }

        return [.. stubs];
    }
}
