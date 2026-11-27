// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Shaders.Translation;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// Finds what a captured dispatch reaches (<see cref="DispatchCaptures"/>), on the game's thread as it records it: the
/// tables its programs read (<see cref="TableReaches"/> by SGPR, and every table the root signature declares, as far as
/// a bind mirrors it), their window bytes, the game memory each V# with a console base names, the views each record's
/// desktop index names (<see cref="ViewLiveness"/>'s notes, a texture whole), and the memory of each root CBV, SRV
/// and UAV (whose size the root does not say).
/// </summary>
internal static unsafe class CapturePlanner
{
    /// <summary>A record's dwords: base low, base high with the V# stride, num_records, the dword with the type.</summary>
    private const int BaseHighDword = 1;
    private const int RecordsDword = 2;
    private const int TypeDword = 3;

    /// <summary>SQ_RSRC_IMG_* types start at 8 in dword 3 bits 28-31; below is a V# (SQ_RSRC_BUF).</summary>
    private const int TypeShift = 28;
    private const uint FirstImageType = 8;
    private const uint HighMask = 0xFFFF;
    private const int StrideShift = 16;
    private const uint StrideMask = 0x3FFF;
    private const int HighShift = 32;

    /// <summary>Bytes read at a root CBV (D3D12_REQ_CONSTANT_BUFFER_ELEMENT_COUNT 4096 x 16) and at a root SRV or UAV.</summary>
    private const ulong RootConstantBufferBytes = 1 << 16;
    private const ulong RootViewBytes = 1 << 20;

    /// <summary>Bytes kept of one V#'s range.</summary>
    private const ulong MaxRangeBytes = 64UL << 20;

    /// <summary>ID3D12Device::GetDescriptorHandleIncrementSize and D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV (d3d12.h).</summary>
    private const int IncrementSlot = 15;
    private const int ViewHeapType = 0;

    /// <summary>What a capture reaches from the user data of <paramref name="list"/>'s compute bind point.</summary>
    public static CapturePlan Plan(XboxObject* list, string pipeline)
    {
        var d3d12x = ShimContext.Runtime.D3D12X;
        var window = d3d12x.Bindings.Window;
        var bindings = CommandListBindings.Of(list);
        var userData = new ReadOnlySpan<uint>(bindings->Compute, CommandListBindings.UserDataDwords).ToArray();
        var plan = new CapturePlan(pipeline, userData, UserDataConstants.Enabled, window.Bytes);

        if (window.Data == null || window.ViewHeap == 0)
        {
            plan.Notes.Add("no descriptor window yet: no table was captured");

            return plan;
        }

        nint device = d3d12x.Objects.DesktopDevice;
        uint increment = ((delegate* unmanaged<nint, int, uint>)ComObject.Slot(device, IncrementSlot))(device, ViewHeapType);
        var heap = (DesktopCalls.HeapCpuStart(window.ViewHeap), increment);

        foreach (var (pointer, records) in Tables(plan, bindings->ComputeLayout))
        {
            Table(list, plan, window, heap, pointer, records);
        }

        RootViews(plan, bindings->ComputeLayout);

        return plan;
    }

    /// <summary>The tables by window offset, with the records read past each: the programs' reach and the root's tables.</summary>
    private static Dictionary<uint, uint> Tables(CapturePlan plan, RootLayout* layout)
    {
        var tables = new Dictionary<uint, uint>();

        foreach (var entry in TableReaches.OfNames(plan.Pipeline))
        {
            if (entry.Sgpr >= 0 && entry.Sgpr < plan.UserData.Length)
            {
                AddTable(tables, plan.UserData[entry.Sgpr], entry.Records);
            }
        }

        for (uint parameter = 0; layout != null && parameter < (uint)layout->Count; parameter++)
        {
            uint sgpr = UserData.ComputeBase + layout->Offsets[parameter];

            if (layout->Kind(parameter) == RootParameterKind.Table && sgpr < plan.UserData.Length)
            {
                AddTable(tables, plan.UserData[sgpr], DescriptorWindow.MirroredRecords(layout->Extents[parameter]));
            }
        }

        return tables;
    }

    private static void AddTable(Dictionary<uint, uint> tables, uint pointer, uint records)
    {
        if (pointer % DescriptorWindow.ViewRecordBytes != 0)
        {
            return;
        }

        records = Math.Min(records, DescriptorWindow.MaxTableRecords);
        tables[pointer] = Math.Max(records, tables.GetValueOrDefault(pointer));
    }

    /// <summary>A table's window bytes, then for a view table what each of its records names.</summary>
    private static void Table(XboxObject* list, CapturePlan plan, DescriptorWindow window, (nint Start, uint Increment) heap, uint pointer, uint records)
    {
        ulong end = Math.Min((ulong)pointer + ((ulong)records * DescriptorWindow.ViewRecordBytes), window.Bytes);

        if (pointer >= end)
        {
            return;
        }

        plan.Patches.Add((pointer, new ReadOnlySpan<byte>(window.Data + pointer, (int)(end - pointer)).ToArray()));

        for (ulong offset = pointer; offset < end && offset < DescriptorWindow.NullTableBase; offset += DescriptorWindow.ViewRecordBytes)
        {
            Record(list, plan, heap, (uint*)(window.Data + offset));
        }
    }

    /// <summary>A V# with a console base: its range of game memory; any other record: the view its desktop index names.</summary>
    private static void Record(XboxObject* list, CapturePlan plan, (nint Start, uint Increment) heap, uint* record)
    {
        uint high = record[BaseHighDword] & HighMask;
        bool buffer = record[TypeDword] >> TypeShift < FirstImageType;

        if (buffer && high != 0)
        {
            ulong stride = (record[BaseHighDword] >> StrideShift) & StrideMask;
            ulong bytes = record[RecordsDword] * Math.Max(stride, 1);
            ulong address = record[0] | ((ulong)high << HighShift);

            if (bytes != 0)
            {
                AddMemory(plan, address, Math.Min(bytes, MaxRangeBytes));
            }

            return;
        }

        uint index = record[DescriptorWindow.MirroredDesktopDword];

        if (index >= DescriptorWindow.ReservedViews && index < DescriptorWindow.MaxViews && plan.SeenIndices.Add(index))
        {
            CaptureViews.View(list, plan, index, heap.Start + (nint)((ulong)index * heap.Increment));
        }
    }

    /// <summary>
    /// Each root CBV, SRV and UAV of the compute root: a range at its address; and each pair of root constants that
    /// holds a mapped console address (a pointer the game passes as constants).
    /// </summary>
    private static void RootViews(CapturePlan plan, RootLayout* layout)
    {
        for (uint parameter = 0; layout != null && parameter < (uint)layout->Count; parameter++)
        {
            var kind = layout->Kind(parameter);
            uint sgpr = UserData.ComputeBase + layout->Offsets[parameter];

            if (kind == RootParameterKind.Constants)
            {
                ConstantPointers(plan, sgpr, layout->Sizes[parameter]);

                continue;
            }

            if (kind is not (RootParameterKind.ConstantBuffer or RootParameterKind.ShaderResource or RootParameterKind.UnorderedAccess) || sgpr + 1 >= plan.UserData.Length)
            {
                continue;
            }

            ulong address = plan.UserData[sgpr] | ((ulong)plan.UserData[sgpr + 1] << HighShift);
            ulong bytes = kind == RootParameterKind.ConstantBuffer ? RootConstantBufferBytes : RootViewBytes;
            AddMemory(plan, address, bytes);
        }
    }

    /// <summary>Consecutive constants (low, high) whose address the address map has a page for: <see cref="RootViewBytes"/> there.</summary>
    private static void ConstantPointers(CapturePlan plan, uint first, uint count)
    {
        var views = ShimContext.Runtime.D3D12X.MemoryViews;

        for (uint sgpr = first; sgpr + 1 < first + count && sgpr + 1 < plan.UserData.Length; sgpr++)
        {
            uint high = plan.UserData[sgpr + 1];
            ulong address = plan.UserData[sgpr] | ((ulong)high << HighShift);

            if (high != 0 && high < AddressMap.Spans && views.Maps(address, 1))
            {
                AddMemory(plan, address, RootViewBytes);
            }
        }
    }

    private static void AddMemory(CapturePlan plan, ulong address, ulong bytes)
    {
        if (address != 0)
        {
            plan.Memory.Add((address, bytes));
        }
    }
}
