// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.Resources;
using FiveStar.Loader.Shims.Xg;

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// The V# fields a buffer view's record carries in dwords 1 to 3 (<see cref="BufferRecord"/>; docs/d3d12x-shim.md,
/// Descriptor window), as AMD's driver lays out such descriptors: a constant buffer as float4 rows (stride 16, the
/// Scaleform batch programs read row batch * 4 + k), a structured view by its structure, a typed view by its format's
/// element and format, a raw view in bytes. Counts come from the game's desc, before the view is fitted to the
/// desktop buffer. Dword 3 carries the OOB_SELECT (bits 28-29) of the view's kind as the console's D3D12 writes it:
/// raw 3 (offset + soffset against bytes), structured 0 (index against records, offset within the structure), typed and
/// constant buffers 1 (index only); translated programs check accesses by it (docs/shader-translation.md#resources).
/// </summary>
internal static unsafe class BufferRecords
{
    /// <summary>Bytes of a constant-buffer row (a float4).</summary>
    public const uint ConstantBufferRow = 16;

    /// <summary>OOB_SELECT in dword 3: raw, structured with the offset checked, index only.</summary>
    private const uint RawBounds = 3u << 28;
    private const uint StructuredBounds = 0;
    private const uint IndexBounds = 1u << 28;

    /// <summary>Bytes of an element of a raw view.</summary>
    private const uint RawElement = 4;

    /// <summary>
    /// <c>--buffer-records-heap-index</c>: buffer records keep the desktop heap index in dword 0 instead of the console
    /// base address (<see cref="BufferRecord.At"/>), as before.
    /// </summary>
    public static bool HeapIndexOnly { get; set; }

    /// <summary>
    /// A read-only view's record (SRV, CBV, slot 70) with its console base (<see cref="BufferRecord.At"/>) when the
    /// address map reaches the same desktop memory the view's resource is: the address table's range holding the base
    /// is <paramref name="desktop"/> (any range for 0). Else, and for every UAV, the heap index stays: the GPU's writes
    /// and the command processor's reads (indirect arguments, copies, index buffers) keep going through the desktop
    /// resource. With every record based (3ac3312), boots hung or crashed at 120-130 s: writes reached memory through
    /// the address map that the desktop resources the command processor reads did not show (a placed buffer is its first
    /// page run, as its pages were when it was placed), and pool memory the game reuses.
    /// </summary>
    [HotPath]
    public static BufferRecord ReadOnly(BufferRecord record, ulong address, ulong firstElement, nint desktop)
    {
        var based = record.At(address, firstElement);

        if (based.Base == 0 || !ShimContext.Runtime.D3D12X.PlacedResources.Addresses.TryFind(based.Base, out var range)
            || (desktop != 0 && range.Resource != desktop))
        {
            return record;
        }

        return based;
    }

    /// <summary>The console GPU address of a resource the game placed (slot 133); 0 for one the desktop placed.</summary>
    [HotPath]
    public static ulong GameAddress(nint resource)
    {
        var wrapper = ShimContext.Runtime.D3D12X.Objects.AsResource(resource);

        if (wrapper == null)
        {
            return 0;
        }

        return wrapper->GameAddress;
    }

    /// <summary>The first element of a D3D12_SHADER_RESOURCE_VIEW_DESC or (<paramref name="unordered"/>) UAV desc of a buffer; 0 otherwise.</summary>
    [HotPath]
    public static ulong FirstElement(void* description, bool unordered)
    {
        var view = (BufferViewDescription*)description;

        if (view == null || view->Dimension != BufferViewDescription.BufferDimension)
        {
            return 0;
        }

        if (unordered)
        {
            return view->UavFirstElement;
        }

        return view->SrvFirstElement;
    }

    /// <summary>The record of a constant buffer of <paramref name="size"/> bytes.</summary>
    [HotPath]
    public static BufferRecord ConstantBuffer(uint size)
    {
        return new BufferRecord(ConstantBufferRow, (size + ConstantBufferRow - 1) / ConstantBufferRow, BufferFormatCodes.Float4 | IndexBounds);
    }

    /// <summary>The record of a D3D12_SHADER_RESOURCE_VIEW_DESC; none for a texture view or no desc.</summary>
    [HotPath]
    public static BufferRecord ShaderResource(void* description)
    {
        var view = (BufferViewDescription*)description;

        if (view == null || view->Dimension != BufferViewDescription.BufferDimension)
        {
            return BufferRecord.None;
        }

        return Buffer(view->Format, view->SrvStride, view->SrvElements, view->SrvFlags);
    }

    /// <summary>The record of a D3D12_UNORDERED_ACCESS_VIEW_DESC; none for a texture view or no desc.</summary>
    [HotPath]
    public static BufferRecord UnorderedAccess(void* description)
    {
        var view = (BufferViewDescription*)description;

        if (view == null || view->Dimension != BufferViewDescription.BufferDimension)
        {
            return BufferRecord.None;
        }

        return Buffer(view->Format, view->UavStride, view->UavElements, view->UavFlags);
    }

    [HotPath]
    private static BufferRecord Buffer(int format, uint structure, uint elements, int flags)
    {
        if ((flags & BufferViewDescription.RawFlag) != 0)
        {
            return new BufferRecord(0, elements * RawElement, BufferFormatCodes.Dword | RawBounds);
        }

        if (structure != 0)
        {
            return new BufferRecord(structure, elements, BufferFormatCodes.Dword | StructuredBounds);
        }

        if (format == ViewFormats.Unknown)
        {
            return new BufferRecord(0, 0, BufferFormatCodes.None);
        }

        return new BufferRecord((uint)XgFormats.Find((uint)format).BytesPerElement, elements, BufferFormatCodes.Of(format) | IndexBounds);
    }
}
