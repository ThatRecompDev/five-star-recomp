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

    /// <summary>
    /// Pages whose bytes can be read as code. PAGE_EXECUTE alone is not one: the loader maps execute-only sections, and
    /// a read there faults like an uncommitted page.
    /// </summary>
    /// <summary>What <see cref="LoadedImage.CodePages"/> holds for a page: not asked yet, readable code, not readable.</summary>
    private const byte UnknownPage = 0;
    private const byte ReadablePage = 1;
    private const byte UnreadablePage = 2;

    private const uint ReadableCode =Win32Constants.PageExecuteRead | Win32Constants.PageExecuteReadWrite | Win32Constants.PageExecuteWriteCopy;

    /// <summary>
    /// Up to <paramref name="count"/> return addresses into mapped images above the caller, innermost first. It runs on
    /// every wait, signal and wake-up the wait-object shims see (thousands a second in the world), so a stack value
    /// outside every image costs one compare, the images are walked without an enumerator, and a page's readability
    /// is asked of the system once (<see cref="CallBytesReadable(LoadedImage, nint)"/>): w104 spent half of a busy
    /// game thread in NtQueryVirtualMemory under WaitForSingleObject.
    /// </summary>
    public static nint[] Scan(ImageTable images, int count)
    {
        nuint low;
        nuint high;
        Kernel32.GetCurrentThreadStackLimits(&low, &high);

        var all = images.All;
        var (first, last) = Bounds(all);
        nint here = 0;
        nint* slot = &here;
        nint* end = (nint*)Math.Min((ulong)high, (ulong)slot + ScannedBytes);
        var found = new List<nint>(count);

        for (; slot < end && found.Count < count; slot++)
        {
            nint value = *slot;

            if ((ulong)value < first || (ulong)value >= last)
            {
                continue;
            }

            // The section test first: stacks hold many pointers into an image's data.
            var image = Find(all, value);

            if (image is not null && InCode(image, value) && CallBytesReadable(image, value) && AfterCall((byte*)value))
            {
                found.Add(value);
            }
        }

        return [.. found];
    }

    /// <summary>The lowest image base and the highest image end: a value outside them is in no image.</summary>
    internal static (ulong First, ulong Last) Bounds(IReadOnlyList<LoadedImage> images)
    {
        ulong first = ulong.MaxValue;
        ulong last = 0;

        for (int i = 0; i < images.Count; i++)
        {
            first = Math.Min(first, (ulong)images[i].Base);
            last = Math.Max(last, (ulong)images[i].Base + images[i].Size);
        }

        return (first, last);
    }

    internal static LoadedImage? Find(IReadOnlyList<LoadedImage> images, nint value)
    {
        for (int i = 0; i < images.Count; i++)
        {
            if (images[i].Contains(value))
            {
                return images[i];
            }
        }

        return null;
    }

    /// <summary>Whether the 6 bytes before <paramref name="value"/> lie in executable sections of <paramref name="image"/>.</summary>
    internal static bool InCode(LoadedImage image, nint value)
    {
        return image.InCode(value - IndirectCall) && image.InCode(value - 1);
    }

    /// <summary>
    /// Whether the 6 bytes before <paramref name="value"/>, in <paramref name="image"/>'s code (<see cref="InCode"/>
    /// holds), are committed readable code (<see cref="ReadableCode"/>): an image's range has pages that are not (a
    /// discarded section, an execute-only section), and a read there killed a boot inside a wait. Each page is asked of
    /// the system once and kept in <see cref="LoadedImage.CodePages"/>: a code section's protection is set once, when
    /// the image is mapped (SectionProtector), and an image leaves the table before it is unmapped.
    /// </summary>
    internal static bool CallBytesReadable(LoadedImage image, nint value)
    {
        return CallBytesReadable(image.CodePages, image.Base, value);
    }

    /// <summary>The same over <paramref name="pages"/>, one state per page from <paramref name="start"/>.</summary>
    internal static bool CallBytesReadable(byte[] pages, nint start, nint value)
    {
        return PageReadable(pages, start, value - IndirectCall) && PageReadable(pages, start, value - 1);
    }

    private static bool PageReadable(byte[] pages, nint start, nint address)
    {
        int page = (int)((ulong)(address - start) / Win32Constants.PageSize);
        byte state = Volatile.Read(ref pages[page]);

        if (state == UnknownPage)
        {
            state = UnreadablePage;

            if (GameHangWatch.IsReadable((ulong)address, ReadableCode))
            {
                state = ReadablePage;
            }

            Volatile.Write(ref pages[page], state);
        }

        return state == ReadablePage;
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
