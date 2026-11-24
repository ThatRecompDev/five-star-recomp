// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// The memory waits a command list recorded since its last Reset (slots 73 and 74, <see cref="MemoryWaits"/>), kept in
/// its wrapper, and the last addresses its bottom-of-pipe writes went to: a wait on an address the same list wrote
/// before it is met by the list's own order and is not kept. A wait recorded after draws, dispatches or indirect
/// executions is not kept either: the desktop can only hold a whole list, and holding the work before the wait as well
/// can close a cycle the console does not have (that work may be what the writer of the value waits for).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ListMemoryWaits
{
    /// <summary>Offset in the command-list wrapper, past <see cref="Binding.CommandListBindings"/> (0x800 to 0xC30).</summary>
    public const int Offset = 0xC40;

    /// <summary>Waits a recording keeps; later ones are counted in <see cref="Dropped"/>.</summary>
    public const int Capacity = 8;

    /// <summary>Write addresses remembered, the newest ones.</summary>
    public const int WriteCapacity = 8;

    public int Count;
    public int Dropped;
    public int MidList;
    public int Writes;
    public fixed ulong Address[Capacity];
    public fixed ulong Value[Capacity];
    public fixed ulong Mask[Capacity];
    public fixed int Comparison[Capacity];
    public fixed int Bytes[Capacity];
    public fixed ulong Written[WriteCapacity];

    public static ListMemoryWaits* Of(XboxObject* list)
    {
        return (ListMemoryWaits*)((byte*)list + Offset);
    }

    /// <summary>A new recording.</summary>
    [HotPath]
    public void Reset()
    {
        Count = 0;
        Dropped = 0;
        MidList = 0;
        Writes = 0;
    }

    /// <summary>A bottom-of-pipe write to <paramref name="address"/> was recorded.</summary>
    [HotPath]
    public void Wrote(ulong address)
    {
        Written[Writes % WriteCapacity] = address;
        Writes++;
    }

    /// <summary>
    /// Keeps a wait; false when the list wrote that address before (met in order), recorded work before it
    /// (<paramref name="workBefore"/>) or has no room left.
    /// </summary>
    [HotPath]
    public bool Record(ulong address, int bytes, int comparison, ulong value, ulong mask, int workBefore)
    {
        if (workBefore != 0)
        {
            MidList++;

            return false;
        }

        int written = Writes;

        if (written > WriteCapacity)
        {
            written = WriteCapacity;
        }

        for (int i = 0; i < written; i++)
        {
            if (Written[i] == address)
            {
                return false;
            }
        }

        if (Count == Capacity)
        {
            Dropped++;

            return false;
        }

        Address[Count] = address;
        Bytes[Count] = bytes;
        Comparison[Count] = comparison;
        Value[Count] = value;
        Mask[Count] = mask;
        Count++;

        return true;
    }
}
