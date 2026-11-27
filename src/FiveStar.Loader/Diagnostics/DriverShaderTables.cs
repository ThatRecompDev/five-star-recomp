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

    private static string _last = string.Empty;
    private static Func<nint>? _device;

    /// <summary>Starts the watch thread; <paramref name="device"/> gives the desktop device, 0 until it is made.</summary>
    public static void Start(Func<nint> device, LoaderLog log)
    {
        _device = device;
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

            if (now.Length != 0 && now != _last)
            {
                _last = now;
                log.Info($"  [d3dkmt] driver shader tables: {now}");
            }
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
