// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using FiveStar.Loader.Diagnostics;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// <c>--watch-4g-lines</c>: two world loads faulted on game records overwritten just past a 4 GiB line of the game's
/// GPU-writable heap (0x252_0000_2840, 0x157_0000_0030) with consecutive values, as a GPU store whose address lost its
/// low dword would write them. The first <see cref="WatchedBytes"/> after every 4 GiB line inside each GPU-writable
/// game allocation are snapshot when found and compared every <see cref="PollMilliseconds"/> ms; a change is logged
/// with its dword runs, old and new bytes (flagging a run of consecutive values), and every queue's executions retired
/// since the last check (<see cref="QueueProgress.DescribeSince"/>, their last pipelines). A change with no execution
/// retired since is the CPU's (the game keeps objects there): it is counted, and logged only when it holds an index run.
/// </summary>
internal static unsafe class FourGiBLineWatch
{
    /// <summary>Consecutive values that make a change look like an index array.</summary>
    public const int IndexRun = 4;

    /// <summary>Bytes watched after each line: one page of the address map.</summary>
    private const int WatchedBytes = 64 * Kibibyte;
    private const int Kibibyte = 1024;

    private const int PollMilliseconds = 10;
    private const int ScanMilliseconds = 2000;
    private const int DetailedReports = 32;
    private const int ShownRuns = 4;
    private const int ShownBytes = 64;

    private static readonly Dictionary<ulong, byte[]> Snapshots = [];
    private static LoaderLog? _log;
    private static ulong[] _since = [];
    private static long _gpuChanges;
    private static long _cpuChanges;
    private static long _reports;

    /// <summary>Lines watched and changes seen, for the exit summary.</summary>
    public static string Summary => $"[4g lines] {Snapshots.Count} lines watched, {Interlocked.Read(ref _gpuChanges)} changes with GPU work retired, {Interlocked.Read(ref _cpuChanges)} without";

    /// <summary>Whether the watch runs.</summary>
    public static bool Started { get; private set; }

    public static void Start(LoaderLog log)
    {
        _log = log;
        Started = true;
        var thread = new Thread(Watch) { IsBackground = true, Name = "fivestar 4g lines" };
        thread.Start();
        log.Info($"[4g lines] watching the first {WatchedBytes / Kibibyte} KiB after each 4 GiB line of GPU-writable game memory every {PollMilliseconds} ms");
    }

    private static void Watch()
    {
        long scanned = long.MinValue;

        while (true)
        {
            long now = Environment.TickCount64;

            if (now - scanned >= ScanMilliseconds)
            {
                Scan();
                scanned = now;
            }

            Check();
            Thread.Sleep(PollMilliseconds);
        }
    }

    /// <summary>Takes a snapshot of each line of the GPU-writable allocations not watched yet.</summary>
    private static void Scan()
    {
        foreach (var (start, bytes) in ShimContext.Runtime.XMemPhysical.Graphics.Writable())
        {
            foreach (ulong line in LineChanges.Lines((ulong)start, bytes))
            {
                int watched = (int)Math.Min(WatchedBytes, (ulong)start + bytes - line);

                var snapshot = Snapshots.ContainsKey(line) ? null : Read(line, watched);

                if (snapshot is not null)
                {
                    Snapshots[line] = snapshot;
                    _log?.Info($"[4g lines] watching 0x{line:x}, {watched} bytes into the allocation at 0x{start:x}");
                }
            }
        }
    }

    private static void Check()
    {
        var completed = QueueProgress.Completed();
        bool retired = Retired(completed);

        foreach (var (line, before) in Snapshots.ToArray())
        {
            var after = Read(line, before.Length);

            if (after is null)
            {
                Snapshots.Remove(line);

                continue;
            }

            var runs = LineChanges.Changed(before, after);

            if (runs.Count != 0)
            {
                Changed(line, before, after, runs, retired);
                Snapshots[line] = after;
            }
        }

        _since = completed;
    }

    private static void Changed(ulong line, byte[] before, byte[] after, List<(int Offset, int Bytes)> runs, bool retired)
    {
        bool index = runs.Exists(run => LineChanges.ConsecutiveValues(after.AsSpan(run.Offset, run.Bytes)).Count >= IndexRun);

        if (retired)
        {
            Interlocked.Increment(ref _gpuChanges);
        }
        else
        {
            Interlocked.Increment(ref _cpuChanges);
        }

        long count = Interlocked.Increment(ref _reports);

        if (!index && (!retired || (count > DetailedReports && (count & (count - 1)) != 0)))
        {
            return;
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"[4g lines] #{count}: 0x{line:x} changed in {runs.Count} runs");

        foreach (var run in runs.Take(ShownRuns))
        {
            text.Append("\n      ").Append(LineChanges.Describe(before, after, run, ShownBytes));
        }

        if (retired)
        {
            text.Append("\n      executions retired since the last check:");
            QueueProgress.DescribeSince(text, _since);
        }
        else
        {
            text.Append("\n      no execution retired since the last check: the CPU wrote it");
        }

        _log?.Warn(text.ToString());
    }

    /// <summary>Whether a queue finished work since the last check.</summary>
    private static bool Retired(ulong[] completed)
    {
        for (int q = 0; q < completed.Length; q++)
        {
            ulong seen = 0;

            if (q < _since.Length)
            {
                seen = _since[q];
            }

            if (completed[q] > seen)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The bytes at <paramref name="line"/> through the kernel; null when the game freed them.</summary>
    private static byte[]? Read(ulong line, int bytes)
    {
        var data = new byte[bytes];
        nuint done = 0;

        fixed (byte* to = data)
        {
            if (Native.Kernel32.ReadProcessMemory(Native.Kernel32.GetCurrentProcess(), (nint)line, to, (nuint)bytes, &done) == 0 || done != (nuint)bytes)
            {
                return null;
            }
        }

        return data;
    }
}
