// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace FiveStar.Loader.Native;

/// <summary>
/// VirtualAlloc2 with MEM_ADDRESS_REQUIREMENTS (memoryapi.h, winnt.h): a reservation or allocation at an address
/// aligned to <see cref="Alignment"/>. The game rebuilds addresses in its XMemVirtualAlloc memory as the base's high
/// dword with a 32-bit offset, as on the console, where those bases sit on 4 GiB lines: three world loads faulted on
/// such addresses (0x157_0000_0030, 0x252_0000_2840, 0x1ce_0000_c000) one 4 GiB line below their allocations.
/// </summary>
internal static unsafe class AlignedReservations
{
    /// <summary>A 4 GiB line: an address on one has a low dword of 0.</summary>
    public const ulong Alignment = 1UL << 32;

    /// <summary>MemExtendedParameterAddressRequirements (winnt.h MEM_EXTENDED_PARAMETER_TYPE).</summary>
    private const ulong AddressRequirementsType = 1;

    /// <summary>
    /// A range of <paramref name="size"/> bytes on a 4 GiB line, with VirtualAlloc2's <paramref name="allocationType"/>
    /// and <paramref name="protect"/>; 0 when the system has none (the caller then allocates anywhere).
    /// </summary>
    public static nint Allocate(nint process, nuint size, uint allocationType, uint protect)
    {
        return Allocate(process, size, allocationType, protect, Alignment);
    }

    /// <summary>The same on a multiple of <paramref name="alignment"/>, a power of two of 64 KiB or more.</summary>
    public static nint Allocate(nint process, nuint size, uint allocationType, uint protect, ulong alignment)
    {
        var requirements = new AddressRequirements { Alignment = alignment };
        var parameter = new ExtendedParameter { Type = AddressRequirementsType, Pointer = &requirements };

        return KernelBase.VirtualAlloc2(process, 0, size, allocationType, protect, &parameter, 1);
    }

    /// <summary>winnt.h MEM_ADDRESS_REQUIREMENTS.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AddressRequirements
    {
        public nint LowestStartingAddress;
        public nint HighestEndingAddress;
        public ulong Alignment;
    }

    /// <summary>winnt.h MEM_EXTENDED_PARAMETER: an 8-bit type in a 64-bit field, then the value.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedParameter
    {
        public ulong Type;
        public AddressRequirements* Pointer;
    }
}
