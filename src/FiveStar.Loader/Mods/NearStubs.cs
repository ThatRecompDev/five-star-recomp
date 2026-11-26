// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Native;

namespace FiveStar.Loader.Mods;

/// <summary>
/// Stubs of patched code within a rel32 jump of their image: each stub gets a page of its own just below the image (the
/// first free 64 KiB step down, at most <see cref="Reach"/> away), written, then made read-execute.
/// </summary>
internal static unsafe class NearStubs
{
    /// <summary>How far below the image a stub may go: well within a rel32 jump from any byte of a 160 MB image.</summary>
    private const long Reach = 1L << 30;

    private const long Step = 1L << 16;
    private const int Rel32Jump = 5;
    private const byte JumpOpcode = 0xE9;

    /// <summary>
    /// Places <paramref name="body"/> and a jump to <paramref name="next"/> near <paramref name="imageBase"/>; returns the
    /// stub's address, 0 when no page in reach was free.
    /// </summary>
    public static nint Place(nint imageBase, ReadOnlySpan<byte> body, nint next)
    {
        nint page = 0;

        // From the 64 KiB step below the image down; never address 0, which VirtualAlloc takes as "anywhere" (the stub
        // then landed far from a low image).
        long start = ((long)imageBase & ~(Step - 1)) - Step;
        long floor = Math.Max(Step, (long)imageBase - Reach);

        for (long at = start; at >= floor && page == 0; at -= Step)
        {
            page = Kernel32.VirtualAlloc((nint)at, Win32Constants.PageSize, Win32Constants.MemReserveCommit, Win32Constants.PageReadWrite);
        }

        if (page == 0)
        {
            return 0;
        }

        var back = Jump(page + body.Length, next);

        if (back is null)
        {
            return 0;
        }

        var code = new Span<byte>((void*)page, Win32Constants.PageSize);
        body.CopyTo(code);
        back.CopyTo(code[body.Length..]);

        uint previous;
        _ = Kernel32.VirtualProtect(page, Win32Constants.PageSize, Win32Constants.PageExecuteRead, &previous);
        _ = Kernel32.FlushInstructionCache(Kernel32.GetCurrentProcess(), page, Win32Constants.PageSize);

        return page;
    }

    /// <summary>The 5-byte <c>jmp rel32</c> at <paramref name="from"/> to <paramref name="to"/>; null when too far.</summary>
    public static byte[]? Jump(nint from, nint to)
    {
        long distance = (long)to - ((long)from + Rel32Jump);

        if (distance < int.MinValue || distance > int.MaxValue)
        {
            return null;
        }

        var bytes = new byte[Rel32Jump];
        bytes[0] = JumpOpcode;
        BitConverter.TryWriteBytes(bytes.AsSpan(1), (int)distance);

        return bytes;
    }
}
