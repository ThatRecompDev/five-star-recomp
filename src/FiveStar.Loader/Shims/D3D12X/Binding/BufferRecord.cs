// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// The V# fields a buffer view's descriptor record carries besides the heap index in dword 0, which stands where the
/// hardware has the base address (<see cref="BufferRecords"/>). All 0 for a texture view.
/// </summary>
/// <param name="Stride">Bytes an indexed access (idxen) multiplies its index by; 0 for a raw view.</param>
/// <param name="Records">Dword 2: num_records, elements of the stride (bytes when the stride is 0).</param>
/// <param name="Format">Dword 3 as the hardware has it (<see cref="BufferFormatCodes"/>).</param>
internal readonly record struct BufferRecord(uint Stride, uint Records, uint Format)
{
    /// <summary>STRIDE: dword 1 bits 16-29; bits 0-15 (base_hi) stay 0, which marks dword 0 as a heap index.</summary>
    private const int StrideShift = 16;
    private const uint StrideMask = 0x3FFF;

    /// <summary>The base's bits 32-47 in dword 1 bits 0-15 (base_hi).</summary>
    private const int HighShift = 32;
    private const ulong HighMask = 0xFFFF;

    /// <summary>
    /// A console GPU address the view reads at, in place of a heap index: dword 0 then holds its low 32 bits and base_hi
    /// its bits 32-47, so translated programs read through the address map (rdna_prelude buf_view), which follows the
    /// pages the game remaps. 0 for a view over a desktop descriptor.
    /// </summary>
    public ulong GameAddress { get; init; }

    /// <summary>The fields of a descriptor that is no buffer view.</summary>
    public static BufferRecord None => default;

    /// <summary>Dword 1 of the record.</summary>
    public uint Word1 => ((Stride & StrideMask) << StrideShift) | (uint)((GameAddress >> HighShift) & HighMask);
}
