// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// A copy of a thread's stack from <paramref name="Rsp"/> (the live address) up: <paramref name="Bytes"/> bytes at
/// <paramref name="Copy"/>, -1 when nothing was read. An unwind reads it in place of the live stack.
/// </summary>
internal readonly unsafe record struct StackCopy(ulong Rsp, nint Copy, long Bytes)
{
    /// <summary>Whether a return address can be read at <paramref name="address"/> of the copy.</summary>
    public bool Holds(ulong address)
    {
        return address >= (ulong)Copy && address + sizeof(ulong) <= (ulong)Copy + (ulong)Bytes;
    }

    /// <summary>Moves the frame pointer at <paramref name="register"/> into the copy when it points into the copied range of the live stack.</summary>
    public void MoveFramePointer(byte* register)
    {
        ulong value = *(ulong*)register;

        if (value >= Rsp && value < Rsp + (ulong)Bytes)
        {
            *(ulong*)register = (ulong)Copy + (value - Rsp);
        }
    }
}
