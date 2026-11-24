// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// The desktop heaps over XMem page sections (ID3D12Device3::OpenExistingHeapFromAddress), so buffers placed on them
/// read and write the game's physical pages; used by slot-133 buffers and the address map alike. A section of at most
/// <see cref="WindowBytes"/> is one heap over the store's whole-section view. A larger one (the store makes one section
/// per contiguous allocation, and the view of a 6 GiB one is refused with E_INVALIDARG) takes a heap per window of
/// <see cref="WindowBytes"/>, each through its own view of that part of the section. Heaps are opened once.
/// </summary>
internal sealed unsafe class SectionHeaps
{
    /// <summary>Bytes of section one heap opens at most.</summary>
    public const ulong WindowBytes = 1UL << 30;

    /// <summary>ID3D12Device3::OpenExistingHeapFromAddress (d3d12.h).</summary>
    private const int OpenHeapFromAddressSlot = 48;

    private static readonly Guid Device3Iid = new("81dadc15-2bad-4392-93c5-101345c4aa98");
    private static readonly Guid HeapIid = new("6b3b2502-6e51-45b3-90ee-9884265e8df3");

    private readonly object _gate = new();
    private readonly Dictionary<(nint Section, ulong Window), nint> _heaps = [];
    private readonly LoaderLog _log;

    public SectionHeaps(LoaderLog log)
    {
        _log = log;
    }

    /// <summary>
    /// The heap holding byte <paramref name="offset"/> of <paramref name="chunk"/>'s section: <paramref name="heapOffset"/>
    /// is that byte's offset in the heap and <paramref name="heapBytes"/> the heap's size. 0 when the desktop refuses it.
    /// </summary>
    public nint Heap(nint device, XMemPageChunk chunk, ulong offset, out ulong heapOffset, out ulong heapBytes)
    {
        ulong sectionBytes = chunk.Pages * XMemPhysicalMemory.PageSize;
        ulong window = 0;
        ulong start = 0;
        heapBytes = sectionBytes;

        if (sectionBytes > WindowBytes)
        {
            window = offset / WindowBytes;
            start = window * WindowBytes;
            heapBytes = Math.Min(WindowBytes, sectionBytes - start);
        }

        heapOffset = offset - start;

        lock (_gate)
        {
            if (_heaps.TryGetValue((chunk.Section, window), out nint existing))
            {
                return existing;
            }

            nint view = chunk.Window;

            if (sectionBytes > WindowBytes)
            {
                view = KernelBase.MapViewOfFile3(chunk.Section, Kernel32.GetCurrentProcess(), 0, start, (nuint)heapBytes, 0, Win32Constants.PageReadWrite, null, 0);
                _log.Detail($"  [d3d12_xs] heap over section 0x{chunk.Section:x} window {window} (0x{heapBytes:x} bytes at 0x{view:x})");
            }

            nint heap = view == 0 ? 0 : Open(device, view);
            _heaps[(chunk.Section, window)] = heap;

            return heap;
        }
    }

    private nint Open(nint device, nint view)
    {
        Guid device3Iid = Device3Iid;
        nint device3 = 0;
        int hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)ComObject.Slot(device, ComObject.QueryInterfaceSlot))(device, &device3Iid, &device3);

        if (HResult.Failed(hr))
        {
            _log.Warn($"[d3d12_xs] QueryInterface(ID3D12Device3) failed: {HResult.Format(hr)}");

            return 0;
        }

        Guid heapIid = HeapIid;
        nint heap = 0;
        hr = ((delegate* unmanaged<nint, nint, Guid*, nint*, int>)ComObject.Slot(device3, OpenHeapFromAddressSlot))(device3, view, &heapIid, &heap);
        ComObject.Release(device3);

        if (HResult.Failed(hr))
        {
            _log.Warn($"[d3d12_xs] OpenExistingHeapFromAddress(0x{view:x}) failed: {HResult.Format(hr)}");
        }

        return heap;
    }
}
