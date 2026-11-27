// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FiveStar.Loader.Codegen;
using FiveStar.Loader.Loading;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// <c>--d3dkmt-watch</c>: who removes the device. D3D12Core and the display driver reach the graphics kernel through
/// win32u.dll's NtGdiDdDDI* system-call stubs (the D3DKMT* thunks of d3dkmthk.h); a removal that is no GPU timeout
/// shows there first, as a failing call, as D3DKMTMarkDeviceAsError (user mode gives the device up) or as a device
/// state other than active. Each watched stub is redirected to generated code that makes the same system call and,
/// only when it fails (always for MarkDeviceAsError, GetDeviceState and CreateDevice), calls <see cref="Report"/>:
/// the call, its NTSTATUS and the stack are logged, the first times per call and status, then counted. A successful
/// call costs a jump and a few instructions. Only calls with a single pointer argument are watched.
/// </summary>
internal static unsafe class KernelGraphicsCalls
{
    /// <summary>Stacks logged per call and status; later failures are counted (logged at powers of two).</summary>
    public const int StacksPerFailure = 4;

    private const string Library = "win32u.dll";
    private const string Prefix = "NtGdiDdDDI";
    private const string MarkDeviceAsError = "MarkDeviceAsError";
    private const string GetDeviceState = "GetDeviceState";
    private const string CreateDevice = "CreateDevice";

    /// <summary>The stub: mov r10,rcx; mov eax,number; test byte [0x7FFE0308],1; jne; syscall; ret; int 2e; ret.</summary>
    private const int StubLength = 24;
    private const int NumberOffset = 4;
    private const int TestOffset = 8;

    /// <summary>KUSER_SHARED_DATA.SystemCall: nonzero when system calls go through int 2e, which the generated code does not make.</summary>
    private const nint SystemCallFlag = 0x7FFE0308;

    /// <summary>The generated function's frame (shadow space for <see cref="Report"/>, aligned) and its prolog's length.</summary>
    private const byte FrameSize = 0x28;
    private const byte PrologSize = 4;

    /// <summary>mov rax,imm64; jmp rax.</summary>
    private const int PatchLength = 12;

    /// <summary>D3DKMT_GETDEVICESTATE: hDevice, StateType, then the state; D3DKMT_CREATEDEVICE: hDevice after the adapter and the flags.</summary>
    private const int StateBytes = 256;
    private const int StateTypeOffset = 4;
    private const int StateValueOffset = 8;
    private const int CreatedDeviceOffset = 12;
    private const int ShownStateBytes = 40;

    /// <summary>D3DKMT_DEVICESTATE_EXECUTION, _RESET and _PAGE_FAULT; D3DKMT_DEVICEEXECUTION_ACTIVE.</summary>
    private const uint ExecutionState = 1;
    private const uint ResetState = 3;
    private const uint PageFaultState = 5;
    private const uint Active = 1;

    private const int StackDepth = 48;

    /// <summary>Calls with one pointer argument (d3dkmthk.h); a name win32u does not export is skipped.</summary>
    private static readonly string[] Watched =
    [
        MarkDeviceAsError, GetDeviceState, CreateDevice, "DestroyDevice", "SubmitCommand", "SubmitCommandToHwQueue",
        "SubmitWaitForSyncObjectsToHwQueue", "SubmitSignalSyncObjectsToHwQueue", "Render", "Present",
        "PresentMultiPlaneOverlay3", "CreateAllocation", "DestroyAllocation", "DestroyAllocation2", "MakeResident", "Evict",
        "Lock", "Unlock", "Lock2", "Unlock2", "ReclaimAllocations2", "OfferAllocations", "CreateContextVirtual",
        "DestroyContext", "CreateHwQueue", "DestroyHwQueue", "CreateSynchronizationObject", "DestroySynchronizationObject",
        "SignalSynchronizationObjectFromGpu", "SignalSynchronizationObjectFromGpu2", "SignalSynchronizationObjectFromCpu",
        "WaitForSynchronizationObjectFromGpu", "WaitForSynchronizationObjectFromCpu", "MapGpuVirtualAddress",
        "ReserveGpuVirtualAddress", "FreeGpuVirtualAddress", "UpdateGpuVirtualAddress", "CreatePagingQueue",
        "DestroyPagingQueue", "Escape", "QueryVideoMemoryInfo", "ChangeVideoMemoryReservation", "UpdateAllocationProperty",
        "SetAllocationPriority", "QueryAllocationResidency", "OpenResourceFromNtHandle", "InvalidateCache",
        "FlushHeapTransitions", "SetQueuedLimit", "SetContextSchedulingPriority", "SetContextInProcessSchedulingPriority",
        "OpenSyncObjectFromNtHandle2",
    ];

    /// <summary>The stub's bytes before and after its call number.</summary>
    private static readonly byte[] StubHead = [0x4C, 0x8B, 0xD1, 0xB8];
    private static readonly byte[] StubTail = [0xF6, 0x04, 0x25, 0x08, 0x03, 0xFE, 0x7F, 0x01, 0x75, 0x03, 0x0F, 0x05, 0xC3, 0xCD, 0x2E, 0xC3];

    /// <summary>mov [rsp+0x30],rcx (the caller's home space); mov r10,rcx. Then mov eax,number and: syscall; test eax,eax.</summary>
    private static readonly byte[] SaveArgument = [0x48, 0x89, 0x4C, 0x24, 0x30, 0x4C, 0x8B, 0xD1];
    private static readonly byte[] SystemCall = [0x0F, 0x05, 0x85, 0xC0];

    /// <summary>js +5, over add rsp,0x28; ret.</summary>
    private static readonly byte[] SkipUnlessFailed = [0x78, 0x05];

    /// <summary>mov ecx,imm32 (the index). Then: mov edx,eax; mov r8,[rsp+0x30]; lea r9,[rsp+0x28].</summary>
    private static readonly byte[] LoadIndex = [0xB9];
    private static readonly byte[] LoadReportArguments = [0x8B, 0xD0, 0x4C, 0x8B, 0x44, 0x24, 0x30, 0x4C, 0x8D, 0x4C, 0x24, 0x28];

    /// <summary>mov rax,imm64 and jmp rax, around the address.</summary>
    private static readonly byte[] JumpHead = [0x48, 0xB8];
    private static readonly byte[] JumpTail = [0xFF, 0xE0];

    private static readonly string[] ExecutionStates = ["?", "active", "reset", "hung", "stopped", "out of memory", "DMA fault", "DMA page fault"];
    private static readonly ConcurrentDictionary<(int Call, int Status), long> Failures = new();
    private static readonly ConcurrentDictionary<uint, byte> Devices = new();
    private static readonly ConcurrentDictionary<(uint Device, uint Type), ulong> States = new();
    private static readonly CodeHeap Heap = new();

    private static LoaderLog? _log;
    private static nint _getDeviceState;
    private static uint _gaveUp;

    [ThreadStatic]
    private static bool _reporting;

    /// <summary>The mapped images, to name stack frames in them; none until a load.</summary>
    public static ImageTable Images { get; set; } = new();

    /// <summary>Redirects the watched stubs; before any device exists. Stubs of an unknown shape are left alone.</summary>
    public static void Install(LoaderLog log)
    {
        _log = log;
        nint module;

        fixed (char* name = Library)
        {
            module = Kernel32.LoadLibraryExW(name, 0, 0);
        }

        if (module == 0 || *(byte*)SystemCallFlag != 0)
        {
            log.Warn($"[d3dkmt] not watching: {Library} 0x{module:x}, system-call flag {*(byte*)SystemCallFlag}");

            return;
        }

        delegate* unmanaged<int, int, nint, nint, int> report = &Report;
        var patches = new List<(nint Stub, nint Hook)>();

        for (int index = 0; index < Watched.Length; index++)
        {
            nint stub = SystemModules.Export(module, Prefix + Watched[index], 0);

            if (stub == 0 || !IsStub((byte*)stub))
            {
                log.Info($"  [d3dkmt] {Watched[index]}: not watched (stub at 0x{stub:x})");

                continue;
            }

            bool always = Watched[index] is MarkDeviceAsError or GetDeviceState or CreateDevice;
            patches.Add((stub, Emit(index, *(uint*)(stub + NumberOffset), always, (nint)report, log)));

            if (Watched[index] == GetDeviceState)
            {
                _getDeviceState = stub;
            }
        }

        Heap.Seal();

        foreach (var (stub, hook) in patches)
        {
            Redirect(stub, hook);
        }

        log.Info($"  [d3dkmt] watching {patches.Count} of {Watched.Length} graphics kernel calls: failures, MarkDeviceAsError and device states are logged");
    }

    /// <summary>The kernel's state of every device this process made (execution, reset, page fault), after a removal.</summary>
    public static void LogDeviceStates(string sign)
    {
        if (_log is null || _getDeviceState == 0)
        {
            return;
        }

        var text = new StringBuilder();
        byte* state = stackalloc byte[StateBytes];
        _reporting = true;

        foreach (uint device in Devices.Keys)
        {
            foreach (uint type in (uint[])[ExecutionState, ResetState, PageFaultState])
            {
                new Span<byte>(state, StateBytes).Clear();
                *(uint*)state = device;
                *(uint*)(state + StateTypeOffset) = type;
                int status = ((delegate* unmanaged<byte*, int>)_getDeviceState)(state);
                text.Append(CultureInfo.InvariantCulture, $"\n    device 0x{device:x} {StateText(type, state, status)}");
            }
        }

        _reporting = false;
        _log.Error($"[d3dkmt] at {sign}: the graphics kernel's device states:{text}");
        LogCounts();
    }

    /// <summary>
    /// Whether MarkDeviceAsError ran on the calling thread since the last time this was asked: the submission that
    /// thread is in (ID3D12CommandQueue::ExecuteCommandLists) is the one the driver gave the device up in.
    /// </summary>
    public static bool GaveUpOnThisThread()
    {
        uint thread = Volatile.Read(ref _gaveUp);

        return thread != 0 && thread == Kernel32.GetCurrentThreadId() && Interlocked.CompareExchange(ref _gaveUp, 0, thread) == thread;
    }

    /// <summary>Every failing call and status counted so far.</summary>
    public static void LogCounts()
    {
        var text = new StringBuilder();

        foreach (var (key, count) in Failures.OrderBy(pair => pair.Key))
        {
            text.Append(CultureInfo.InvariantCulture, $"\n    {Watched[key.Call]} -> 0x{key.Status:x8}: {count}");
        }

        if (text.Length == 0)
        {
            text.Append(" none");
        }

        _log?.Info($"  [d3dkmt] failed calls so far:{text}");
    }

    private static bool IsStub(byte* stub)
    {
        return new ReadOnlySpan<byte>(stub, StubHead.Length).SequenceEqual(StubHead) && new ReadOnlySpan<byte>(stub + TestOffset, StubLength - TestOffset).SequenceEqual(StubTail);
    }

    /// <summary>
    /// The replacement of one stub: the system call itself, then <see cref="Report"/> with the call's index, status,
    /// argument and the address of its return address when it failed (or always), whose result it returns.
    /// </summary>
    private static nint Emit(int index, uint number, bool always, nint report, LoaderLog log)
    {
        var code = new X64Emitter();
        code.SubRsp(FrameSize);

        code.Data(SaveArgument);
        code.MovEax(number);
        code.Data(SystemCall);

        if (!always)
        {
            code.Data(SkipUnlessFailed);
            code.AddRsp(FrameSize);
            code.Ret();
        }

        code.Data(LoadIndex);
        code.Data32((uint)index);
        code.Data(LoadReportArguments);
        code.MovRax((ulong)report);

        code.CallRax();
        code.AddRsp(FrameSize);
        code.Ret();

        return FramedFunction.Commit(Heap, code, PrologSize, FrameSize, log, "[d3dkmt]");
    }

    private static void Redirect(nint stub, nint hook)
    {
        byte* patch = stackalloc byte[PatchLength];
        JumpHead.CopyTo(new Span<byte>(patch, PatchLength));
        *(nint*)(patch + JumpHead.Length) = hook;
        JumpTail.CopyTo(new Span<byte>(patch + JumpHead.Length + sizeof(nint), JumpTail.Length));

        uint previous;
        _ = Kernel32.VirtualProtect(stub, StubLength, Win32Constants.PageExecuteReadWrite, &previous);
        Buffer.MemoryCopy(patch, (void*)stub, PatchLength, PatchLength);
        _ = Kernel32.VirtualProtect(stub, StubLength, previous, &previous);
        _ = Kernel32.FlushInstructionCache(Kernel32.GetCurrentProcess(), stub, StubLength);
    }

    /// <summary>Called by the generated code after a failed (or always-reported) call; returns its status.</summary>
    [UnmanagedCallersOnly]
    private static int Report(int index, int status, nint argument, nint returnSlot)
    {
        try
        {
            NoteOnce(index, status, (byte*)argument);

            return status;
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "KernelGraphicsCalls.Report");

            return status;
        }
    }

    /// <summary><see cref="Note"/>, unless the thread is already in it (a report's own calls are not reported).</summary>
    private static void NoteOnce(int index, int status, byte* argument)
    {
        if (_reporting)
        {
            return;
        }

        _reporting = true;

        try
        {
            Note(index, status, argument);
        }
        finally
        {
            _reporting = false;
        }
    }

    private static void Note(int index, int status, byte* argument)
    {
        string name = Watched[index];

        if (status >= 0 && name == CreateDevice)
        {
            uint device = *(uint*)(argument + CreatedDeviceOffset);
            Devices[device] = 0;
            _log?.Info($"  [d3dkmt] device 0x{device:x} created{Stack()}");

            return;
        }

        if (status >= 0 && name == GetDeviceState)
        {
            NoteState(argument);

            return;
        }

        if (name == MarkDeviceAsError)
        {
            Volatile.Write(ref _gaveUp, Kernel32.GetCurrentThreadId());

            // D3DKMT_MARKDEVICEASERROR: hDevice, Reason.
            _log?.Error($"[d3dkmt] MarkDeviceAsError(device 0x{*(uint*)argument:x}, reason 0x{*(uint*)(argument + StateTypeOffset):x}) -> 0x{status:x8}: user mode gives the device up{Stack()}");

            return;
        }

        long count = Failures.AddOrUpdate((index, status), 1, (_, previous) => previous + 1);

        if (count <= StacksPerFailure)
        {
            _log?.Warn($"[d3dkmt] {name} -> 0x{status:x8} (#{count}, first words 0x{*(ulong*)argument:x16} 0x{*(ulong*)(argument + sizeof(ulong)):x16}){Stack()}");
        }
        else if ((count & (count - 1)) == 0)
        {
            _log?.Warn($"[d3dkmt] {name} -> 0x{status:x8}: {count} times");
        }
    }

    /// <summary>A device state the runtime or the driver asked for: logged when it changes to anything but active or empty.</summary>
    private static void NoteState(byte* state)
    {
        uint device = *(uint*)state;
        uint type = *(uint*)(state + StateTypeOffset);
        ulong value = *(ulong*)(state + StateValueOffset);
        bool known = States.TryGetValue((device, type), out ulong last);
        States[(device, type)] = value;
        Devices[device] = 0;

        bool quiet = value == 0;

        if (type == ExecutionState)
        {
            quiet = (uint)value == Active;
        }

        if ((known && last == value) || (!known && quiet))
        {
            return;
        }

        _log?.Error($"[d3dkmt] device 0x{device:x} {StateText(type, state, 0)}{Stack()}");
    }

    private static string StateText(uint type, byte* state, int status)
    {
        if (status < 0)
        {
            return $"state type {type}: GetDeviceState -> 0x{status:x8}";
        }

        uint value = *(uint*)(state + StateValueOffset);

        if (type == ExecutionState)
        {
            string name = ExecutionStates[0];

            if (value < ExecutionStates.Length)
            {
                name = ExecutionStates[value];
            }

            return $"execution state {value} ({name})";
        }

        string bytes = Convert.ToHexString(new ReadOnlySpan<byte>(state + StateValueOffset, ShownStateBytes));

        return type switch
        {
            ResetState => $"reset state flags 0x{value:x} (1 hung, 2 other device hung)",
            PageFaultState => $"page fault state {bytes} (sequence number, stage, bind entry, flags, error code, GPU VA)",
            _ => $"state type {type}: {bytes}",
        };
    }

    /// <summary>The calling thread's stack as lines, innermost first (this report's own frames come first), then its managed frames.</summary>
    private static string Stack()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"\n    thread {Environment.CurrentManagedThreadId} (OS {Kernel32.GetCurrentThreadId()}) \"{Thread.CurrentThread.Name}\":");

        foreach (var frame in NativeStack.Walk(Images, StackDepth))
        {
            text.Append(CultureInfo.InvariantCulture, $"\n      0x{frame.Pc:x} {frame.Where}");
        }

        // The managed frames among them by name: the walk above names none of the JIT's code.
        try
        {
            text.Append("\n    managed frames:\n").Append(new System.Diagnostics.StackTrace(false));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            text.Append(CultureInfo.InvariantCulture, $" unread ({exception.GetType().Name})");
        }

        return text.ToString().TrimEnd();
    }
}
