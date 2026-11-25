// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Loading;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// The game frames above a shim on the calling thread, found by scanning its stack for values that are return addresses
/// into a mapped image (the bytes before them a call). A walk with unwind data stops in the shims' generated code (the
/// last-error bridge has none), so the wait objects of <c>--watch-hang</c> named no creator at all (issue #13).
/// </summary>
internal static unsafe class ReturnAddresses
{
    /// <summary>Stack bytes scanned above the caller: the shim's managed frames sit below the game's.</summary>
    private const int ScannedBytes = 64 * 1024;

    /// <summary>The bytes a call ends with before its return address: E8 rel32, FF 15 disp32 (call [rip+d]), FF 90-97 disp32 (call [reg+d32]).</summary>
    private const int LongCall = 5;
    private const int IndirectCall = 6;

    /// <summary>FF 50-57 disp8 (call [reg+d8]), and FF D0-D7 (call reg, with or without a REX prefix before it).</summary>
    private const int ShortCall = 3;
    private const int RegisterCall = 2;

    private const byte CallRelative = 0xE8;
    private const byte Group5 = 0xFF;
    private const byte RipRelative = 0x15;
    private const byte ModMask = 0xF8;
    private const byte DirectRegister = 0xD0;
    private const byte Displacement8 = 0x50;
    private const byte Displacement32 = 0x90;

    /// <summary>Up to <paramref name="count"/> return addresses into mapped images above the caller, innermost first.</summary>
    public static nint[] Scan(ImageTable images, int count)
    {
        nuint low;
        nuint high;
        Kernel32.GetCurrentThreadStackLimits(&low, &high);

        nint here = 0;
        nint* slot = &here;
        nint* end = (nint*)Math.Min((ulong)high, (ulong)slot + ScannedBytes);
        var found = new List<nint>();

        for (; slot < end && found.Count < count; slot++)
        {
            nint value = *slot;
            var image = images.FindByAddress(value);

            if (image is not null && value - IndirectCall >= image.Base && CallBytesReadable(value) && AfterCall((byte*)value))
            {
                found.Add(value);
            }
        }

        return [.. found];
    }

    /// <summary>
    /// Whether the 6 bytes before <paramref name="value"/> are committed: an image's range has pages that are not (a section
    /// window unmapped, a discarded section), and a read there killed a boot inside a wait.
    /// </summary>
    internal static bool CallBytesReadable(nint value)
    {
        ulong first = (ulong)(value - IndirectCall);
        ulong last = (ulong)(value - 1);
        ulong page = (ulong)Environment.SystemPageSize;
        bool samePage = first / page == last / page;

        return GameHangWatch.IsReadable(first) && (samePage || GameHangWatch.IsReadable(last));
    }

    /// <summary>Whether the bytes before <paramref name="address"/> end a call instruction (6 readable bytes before it).</summary>
    public static bool AfterCall(byte* address)
    {
        bool relative = address[-LongCall] == CallRelative;
        bool indirect = address[-IndirectCall] == Group5 && (address[-LongCall] == RipRelative || (address[-LongCall] & ModMask) == Displacement32);
        bool displaced = address[-ShortCall] == Group5 && (address[-RegisterCall] & ModMask) == Displacement8;
        bool register = address[-RegisterCall] == Group5 && (address[-1] & ModMask) == DirectRegister;

        return relative || indirect || displaced || register;
    }
}
