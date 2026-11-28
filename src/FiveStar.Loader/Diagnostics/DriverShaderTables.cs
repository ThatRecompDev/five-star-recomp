// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// <c>--d3dkmt-watch</c>, NVIDIA's user-mode driver 32.0.16.2124 only: the two tables of 256-byte entries the driver
/// grows before a submission (its own report of them reads "Current Size, Max Size, Optional Shader Total, Optional
/// Shader Limit"). w221's removal started in that growth: the resize returned E_INVALIDARG inside
/// ExecuteCommandLists, and the driver gave the device up. The tables are found from the desktop device (a driver
/// object with the known vtable, then its table holder) and read with ReadProcessMemory, so a driver of another
/// layout reads as not found instead of faulting. Logged when they change, at most every
/// <see cref="PollMilliseconds"/> ms, and at a removal.
/// </summary>
internal static unsafe class DriverShaderTables
{
    private const string Driver = "nvwgf2umx.dll";

    /// <summary>The driver device's vtable, the device's table holder, the holder's two tables.</summary>
    private const ulong DeviceVtable = 0x3d0caa0;
    private const int HolderOffset = 0x21e68;
    private const int FirstTableOffset = 8;
    private const int Tables = 2;

    /// <summary>A table: its maximum size in bytes, its entries wanted, its optional shaders' bytes and their limit.</summary>
    private const int MaxSizeOffset = 0x10;
    private const int EntriesOffset = 0x38;
    private const int OptionalOffset = 0x60;
    private const int OptionalLimitOffset = 0x68;
    private const ulong EntryBytes = 256;

    /// <summary>The desktop device's bytes searched for the driver object, and the lowest address a pointer has.</summary>
    private const int DeviceBytes = 0x2000;
    private const ulong LowestPointer = 0x10000;

    private const int PollMilliseconds = 5000;
    private const double MiB = 1024.0 * 1024.0;

    /// <summary>Names of pipelines first used that one log line carries.</summary>
    private const int NamesShown = 8;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, byte> Used = new();
    private static readonly System.Collections.Concurrent.ConcurrentQueue<nint> NewlyUsed = new();
    private static string _last = string.Empty;
    private static Func<nint>? _device;
    private static long _created;
    private static ulong _holder;
    private static nint _searched;
    private static long _loaded;

    /// <summary>Whether pipelines are followed (<see cref="NoteUsed"/>): set by <see cref="Start"/>.</summary>
    public static bool Enabled { get; private set; }

    /// <summary>More for each logged line (the pipelines' own accounting); unset, nothing.</summary>
    public static Func<string>? More { get; set; }

    /// <summary>Names a desktop pipeline for the log; unset, its address.</summary>
    public static Func<nint, string>? Name { get; set; }

    /// <summary>A desktop pipeline was made by CreatePipelineState, or loaded from a pipeline library.</summary>
    public static void NoteCreated(bool loaded)
    {
        if (loaded)
        {
            Interlocked.Increment(ref _loaded);
        }
        else
        {
            Interlocked.Increment(ref _created);
        }
    }

    /// <summary>A desktop pipeline is set on a list: the driver adds a pipeline's shaders to its tables when it is first used.</summary>
    public static void NoteUsed(nint pipeline)
    {
        if (pipeline != 0 && !Used.ContainsKey(pipeline) && Used.TryAdd(pipeline, 0))
        {
            NewlyUsed.Enqueue(pipeline);
        }
    }

    /// <summary>Starts the watch thread; <paramref name="device"/> gives the desktop device, 0 until it is made.</summary>
    public static void Start(Func<nint> device, LoaderLog log)
    {
        _device = device;
        Enabled = true;
        var thread = new Thread(() => Watch(device, log)) { IsBackground = true, Name = "fivestar driver shader tables" };
        thread.Start();
    }

    /// <summary>The tables of <paramref name="device"/> as text; empty when they are not found.</summary>
    public static string Describe(nint device)
    {
        nint module;

        fixed (char* name = Driver)
        {
            module = Kernel32.GetModuleHandleW(name);
        }

        if (device == 0 || module == 0 || !Find(device, (ulong)module + DeviceVtable, out ulong holder))
        {
            return string.Empty;
        }

        var text = new StringBuilder();

        for (int i = 0; i < Tables; i++)
        {
            if (!Read(holder + FirstTableOffset + ((ulong)i * sizeof(ulong)), out ulong table) || !Read(table + EntriesOffset, out ulong entries) || !Read(table + MaxSizeOffset, out ulong max)
                || !Read(table + OptionalOffset, out ulong optional) || !Read(table + OptionalLimitOffset, out ulong limit))
            {
                return string.Empty;
            }

            if (i > 0)
            {
                text.Append("; ");
            }

            double size = (uint)entries * EntryBytes / MiB;
            text.Append(CultureInfo.InvariantCulture, $"table {i}: {(uint)entries} entries = {size:F1} of {max / MiB:F0} MiB, optional shaders {optional / MiB:F1} of {limit / MiB:F0} MiB");
        }

        return text.ToString();
    }

    /// <summary>
    /// The bytes the larger of <paramref name="device"/>'s tables wants now; 0 when no table is found (another
    /// driver).
    /// </summary>
    public static long LargestTableBytes(nint device)
    {
        nint module;

        fixed (char* name = Driver)
        {
            module = Kernel32.GetModuleHandleW(name);
        }

        return LargestTableBytes(device, (ulong)module);
    }

    /// <summary>
    /// As <see cref="LargestTableBytes(nint)"/> with the driver at <paramref name="module"/>. The table holder is looked
    /// for until found and then kept per device, and its tables are read at every call: table 1 may not exist yet at the
    /// first check, and keeping that miss read every later check as "not found" (issue #64: table 1 at 492 of 512 MiB,
    /// no guard line), so a table that is missing counts as empty.
    /// </summary>
    internal static long LargestTableBytes(nint device, ulong module)
    {
        if (device == 0 || module == 0)
        {
            return 0;
        }

        if (device != _searched)
        {
            if (!Find(device, module + DeviceVtable, out ulong holder))
            {
                return 0;
            }

            _holder = holder;
            _searched = device;
        }

        ulong largest = 0;

        for (int i = 0; i < Tables; i++)
        {
            if (Read(_holder + FirstTableOffset + ((ulong)i * sizeof(ulong)), out ulong table) && table >= LowestPointer && Read(table + EntriesOffset, out ulong entries))
            {
                largest = Math.Max(largest, (uint)entries * EntryBytes);
            }
        }

        return (long)largest;
    }

    /// <summary>Forgets the device searched (tests).</summary>
    internal static void Forget()
    {
        _searched = 0;
        _holder = 0;
    }

    /// <summary>The watched device's tables now, for a removal report; empty when not watched or not found.</summary>
    public static string DescribeWatched()
    {
        var device = _device;

        if (device is null)
        {
            return string.Empty;
        }

        return Describe(device());
    }

    private static void Watch(Func<nint> device, LoaderLog log)
    {
        while (true)
        {
            Thread.Sleep(PollMilliseconds);
            string now = Describe(device());
            int added = NewlyUsed.Count;

            if (now.Length == 0 || (now == _last && added == 0))
            {
                continue;
            }

            _last = now;
            var names = new StringBuilder();

            for (int i = 0; i < added && NewlyUsed.TryDequeue(out nint pipeline); i++)
            {
                if (i < NamesShown)
                {
                    names.Append(", ").Append(Name?.Invoke(pipeline) ?? $"0x{pipeline:x}");
                }
            }

            log.Info($"  [d3dkmt] driver shader tables: {now}; pipelines made {Interlocked.Read(ref _created)}, loaded from a library {Interlocked.Read(ref _loaded)}, set on a list {Used.Count} (+{added}{names}){More?.Invoke()}");
        }
    }

    /// <summary>The table holder of the driver object the device points to; false when no pointer of the device leads to one.</summary>
    private static bool Find(nint device, ulong vtable, out ulong holder)
    {
        holder = 0;

        for (int offset = 0; offset < DeviceBytes; offset += sizeof(ulong))
        {
            if (Read((ulong)device + (ulong)offset, out ulong candidate) && candidate >= LowestPointer && Read(candidate, out ulong first) && first == vtable)
            {
                return Read(candidate + HolderOffset, out holder) && holder >= LowestPointer;
            }
        }

        return false;
    }

    private static bool Read(ulong address, out ulong value)
    {
        ulong read = 0;
        nuint count;
        bool done = Kernel32.ReadProcessMemory(Kernel32.GetCurrentProcess(), (nint)address, &read, sizeof(ulong), &count) != 0 && count == sizeof(ulong);
        value = read;

        return done;
    }
}
