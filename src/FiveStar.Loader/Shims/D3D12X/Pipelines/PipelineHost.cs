// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// Builds a heavy pipeline first in a child process (<c>--pipeline-host</c>, <see cref="PipelineHostServer"/>) that a
/// job object holds to a memory limit and that is ended past a time limit, so a driver compile that would take more
/// than the machine has ends there instead of in the game (issue #19: one AMD compute pipeline passed 50 GB). The host
/// is the loader's own executable, so the driver's disk cache it fills serves the loader's create of the same pipeline
/// at once; a cached-PSO blob would not (an RTX 4090 took 95 s and the same 6.8 GB to load the foliage pipeline from
/// its blob in a new process). One host per pipeline; the compile budget admits it like any other compile.
/// </summary>
internal sealed unsafe class PipelineHost
{
    /// <summary>How long a host may compile: the foliage mesh pipeline took 191 s alone on an RTX 4090.</summary>
    public static readonly TimeSpan DefaultTimeLimit = TimeSpan.FromSeconds(600);

    /// <summary>
    /// A host that ended without an answer at or above this share of its limit ran out of memory: allocations come in
    /// pieces, so the last one that failed left the peak short of the limit (a host taking 64 MB at a time under 512 MB
    /// peaked at 455 MB).
    /// </summary>
    private const double NearLimit = 0.75;

    /// <summary>The most a host may take: about four times the plain foliage pipeline's 6.8 GB on NVIDIA, the AMD estimate's cap.</summary>
    private const long NvidiaMemoryCap = 24L << 30;
    private const long AmdMemoryCap = 48L << 30;

    /// <summary>The least a host is given, so a compile on a busy machine is not killed for want of a few GB at its start.</summary>
    private const long MinimumMemoryLimit = 4L << 30;

    // JobObjectExtendedLimitInformation and the JOBOBJECT_EXTENDED_LIMIT_INFORMATION layout (winnt.h, x64):
    // BasicLimitInformation.LimitFlags at 16, ProcessMemoryLimit at 112, PeakProcessMemoryUsed at 128, 144 bytes.
    private const int ExtendedLimitInformation = 9;
    private const int ExtendedLimitBytes = 144;
    private const int LimitFlagsOffset = 16;
    private const int ProcessMemoryLimitOffset = 112;
    private const int PeakProcessMemoryOffset = 128;
    private const uint KillOnJobClose = 0x2000;
    private const uint ProcessMemoryLimit = 0x100;

    private readonly string _executable;
    private int _built;
    private int _killed;
    private int _failed;

    /// <param name="executable">The loader executable.</param>
    public PipelineHost(string executable)
    {
        _executable = executable;
    }

    /// <summary>The host heavy pipelines are built in first (<c>--host-pipelines</c>); null builds them in the loader only.</summary>
    public static PipelineHost? Current { get; set; }

    /// <summary>
    /// The memory limit of a host started with <paramref name="availablePhysical"/> free: what is free less the
    /// compile budget's floor, at most the vendor's cap and at least 4 GB.
    /// </summary>
    public static long MemoryLimit(ulong availablePhysical, uint vendorId)
    {
        long cap = NvidiaMemoryCap;

        if (vendorId == CompileBudget.AmdVendorId)
        {
            cap = AmdMemoryCap;
        }

        long free = (long)Math.Min(availablePhysical, long.MaxValue) - CompileBudget.MinimumPhysicalReserve;

        return Math.Clamp(free, MinimumMemoryLimit, cap);
    }

    /// <summary>Hosts that built their pipeline, that were ended at a limit, and that failed otherwise.</summary>
    public string Summary => $"pipeline host: {Volatile.Read(ref _built)} built, {Volatile.Read(ref _killed)} past a limit, {Volatile.Read(ref _failed)} failed";

    /// <summary>
    /// Builds <paramref name="stream"/> in a host held to <paramref name="memoryLimit"/> bytes and
    /// <paramref name="timeLimit"/>; the stream is not changed.
    /// </summary>
    public PipelineHostResult Build(PipelineHostRequest request, DesktopStream stream, long memoryLimit, TimeSpan timeLimit)
    {
        var result = Run(request, stream, memoryLimit, timeLimit);

        if (result.Outcome == PipelineHostOutcome.Built)
        {
            Interlocked.Increment(ref _built);
        }
        else if (result.Killed)
        {
            Interlocked.Increment(ref _killed);
        }
        else
        {
            Interlocked.Increment(ref _failed);
        }

        return result;
    }

    /// <summary>How a host that gave <paramref name="hresult"/> (null: none) after a peak of <paramref name="peak"/> ended.</summary>
    public static PipelineHostOutcome Classify(int? hresult, long peak, long memoryLimit, bool timedOut)
    {
        if (timedOut)
        {
            return PipelineHostOutcome.KilledTime;
        }

        if (hresult is not null && !HResult.Failed(hresult.Value))
        {
            return PipelineHostOutcome.Built;
        }

        if (hresult == HResult.OutOfMemory || peak >= memoryLimit * NearLimit)
        {
            return PipelineHostOutcome.KilledMemory;
        }

        return PipelineHostOutcome.Failed;
    }

    private PipelineHostResult Run(PipelineHostRequest request, DesktopStream stream, long memoryLimit, TimeSpan timeLimit)
    {
        var clock = Stopwatch.StartNew();
        nint job = CreateJob(memoryLimit);
        Process? process = null;

        try
        {
            process = Start(job);

            if (process is null)
            {
                return new PipelineHostResult(PipelineHostOutcome.Failed, 0, 0, 0);
            }

            var answer = Task.Run(() => Exchange(process, request, stream));
            bool finished = answer.Wait(timeLimit);

            if (!finished)
            {
                Kill(process);
            }

            process.WaitForExit();
            int? hresult = null;

            if (finished)
            {
                hresult = answer.Result;
            }

            long peak = PeakBytes(job);
            var outcome = Classify(hresult, peak, memoryLimit, !finished);

            return new PipelineHostResult(outcome, hresult ?? 0, clock.Elapsed.TotalSeconds, peak);
        }
        finally
        {
            process?.Dispose();
            _ = Kernel32.CloseHandle(job);
        }
    }

    private Process? Start(nint job)
    {
        var start = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(PipelineHostProtocol.Mode);

        try
        {
            var process = Process.Start(start);

            // The host waits for its request before it allocates anything, so the limit holds from its first byte.
            if (process is not null)
            {
                _ = Kernel32.AssignProcessToJobObject(job, process.Handle);
            }

            return process;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Sends the request and reads the HRESULT; null when the host ended without one.</summary>
    private static int? Exchange(Process process, PipelineHostRequest request, DesktopStream stream)
    {
        try
        {
            using (var writer = new BinaryWriter(process.StandardInput.BaseStream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                PipelineHostProtocol.WriteRequest(writer, request, stream);
            }

            using var reader = new BinaryReader(process.StandardOutput.BaseStream);

            return PipelineHostProtocol.ReadAnswer(reader);
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException or InvalidDataException)
        {
            return null;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    private static nint CreateJob(long memoryLimit)
    {
        nint job = Kernel32.CreateJobObjectW(0, 0);
        byte* limits = stackalloc byte[ExtendedLimitBytes];
        new Span<byte>(limits, ExtendedLimitBytes).Clear();
        *(uint*)(limits + LimitFlagsOffset) = KillOnJobClose | ProcessMemoryLimit;
        *(ulong*)(limits + ProcessMemoryLimitOffset) = (ulong)memoryLimit;
        _ = Kernel32.SetInformationJobObject(job, ExtendedLimitInformation, limits, ExtendedLimitBytes);

        return job;
    }

    /// <summary>The most memory a process of the job committed.</summary>
    private static long PeakBytes(nint job)
    {
        byte* limits = stackalloc byte[ExtendedLimitBytes];
        uint returned = 0;

        if (Kernel32.QueryInformationJobObject(job, ExtendedLimitInformation, limits, ExtendedLimitBytes, &returned) == 0)
        {
            return 0;
        }

        return (long)*(ulong*)(limits + PeakProcessMemoryOffset);
    }
}
