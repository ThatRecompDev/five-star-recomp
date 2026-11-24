// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// Console GPU addresses in memory the game committed itself (XMemVirtualAlloc with MEM_COMMIT, such as its 2.2 GiB
/// heap with PAGE_GRAPHICS_READWRITE): on the console the GPU reads any of it at the CPU address. On the first address
/// that misses <see cref="XboxPlacedResources.Addresses"/>, the whole VirtualAlloc allocation around it is opened as a
/// heap (ID3D12Device3::OpenExistingHeapFromAddress) and covered by one placed buffer, whose range joins the table.
/// The console GPU writes only to graphics memory (<see cref="GraphicsAllocations"/>): any other allocation gets a
/// buffer without unordered access, so the address map gives programs no UAV there and their writes are dropped.
/// </summary>
internal sealed unsafe class XboxGameMemoryBuffers
{
    /// <summary>ID3D12Device::CreatePlacedResource, ID3D12Device3::OpenExistingHeapFromAddress, ID3D12Resource::GetGPUVirtualAddress (d3d12.h).</summary>
    private const int CreatePlacedResourceSlot = 29;
    private const int OpenHeapFromAddressSlot = 48;
    private const int GetGpuAddressSlot = 11;

    /// <summary>MEM_PRIVATE: memory from VirtualAlloc, the only kind OpenExistingHeapFromAddress takes (winnt.h).</summary>
    private const uint PrivateMemory = 0x20000;

    /// <summary>D3D12_RESOURCE_FLAG_ALLOW_CROSS_ADAPTER, which a buffer in such a heap needs, and ALLOW_UNORDERED_ACCESS.</summary>
    private const int CrossAdapter = 0x10;
    private const int UnorderedAccess = 0x4;

    /// <summary>D3D12_TEXTURE_LAYOUT_ROW_MAJOR, the layout of every buffer, and the single sample.</summary>
    private const int RowMajor = 1;
    private const int CommonState = 0;

    private static readonly Guid Device3Iid = new("81dadc15-2bad-4392-93c5-101345c4aa98");
    private static readonly Guid HeapIid = new("6b3b2502-6e51-45b3-90ee-9884265e8df3");
    private static readonly Guid ResourceIid = new("696442be-a72e-4059-bc79-5b5c98040fad");

    private readonly object _gate = new();
    private readonly HashSet<nint> _tried = [];
    private readonly XboxObjects _objects;
    private readonly AddressTable _addresses;
    private readonly GraphicsAllocations _graphics;
    private readonly LoaderLog _log;

    public XboxGameMemoryBuffers(XboxObjects objects, AddressTable addresses, GraphicsAllocations graphics, LoaderLog log)
    {
        _objects = objects;
        _addresses = addresses;
        _graphics = graphics;
        _log = log;
    }

    private readonly HashSet<nint> _readOnly = [];

    /// <summary>Whether <paramref name="resource"/> is a buffer this class made over memory the GPU may not write.</summary>
    public bool IsReadOnly(nint resource)
    {
        lock (_gate)
        {
            return _readOnly.Contains(resource);
        }
    }

    /// <summary>The desktop GPU address of a console one; false when no desktop buffer covers it.</summary>
    [HotPath]
    public bool TryTranslate(ulong address, out ulong desktop)
    {
        return _addresses.TryTranslate(address, out desktop) || (TryCover(address) && _addresses.TryTranslate(address, out desktop));
    }

    /// <summary>The desktop buffer range holding console address <paramref name="address"/>; false when none covers it.</summary>
    [HotPath]
    public bool TryFind(ulong address, out AddressRange range)
    {
        return _addresses.TryFind(address, out range) || (TryCover(address) && _addresses.TryFind(address, out range));
    }

    /// <summary>
    /// Covers the allocation holding <paramref name="address"/> with a buffer, once per allocation. Every miss asks, so
    /// the allocation is found without VirtualQuery (<see cref="MemoryRegionInformation"/>): XMem pages are mapped views,
    /// not private memory, and an allocation tried before is not walked again.
    /// </summary>
    private bool TryCover(ulong address)
    {
        nint device = _objects.DesktopDevice;

        if (device == 0 || !MemoryRegionInformation.Query((nint)address, out var region) || (region.Flags & MemoryRegionInformation.PrivateFlag) == 0)
        {
            return false;
        }

        nint allocation = region.AllocationBase;

        lock (_gate)
        {
            if (_tried.Contains(allocation))
            {
                return false;
            }
        }

        if (!region.FullyCommittedPrivate && !CommittedPage((nint)address))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_tried.Add(allocation))
            {
                return false;
            }

            ulong size = AllocationSize(allocation);
            bool writable = GpuWritable(allocation, size);
            nint buffer = CreateBuffer(device, allocation, size, writable);

            if (buffer == 0)
            {
                return false;
            }

            if (!writable)
            {
                _readOnly.Add(buffer);
            }

            ulong gpu = ((delegate* unmanaged<nint, ulong>)ComObject.Slot(buffer, GetGpuAddressSlot))(buffer);
            _addresses.Add(new AddressRange((ulong)allocation, size, gpu, buffer));
            string access = writable ? string.Empty : ", read-only";
            _log.Detail($"  [d3d12_xs] game memory 0x{allocation:x} (0x{size:x} bytes) on the GPU at 0x{gpu:x}{access}");

            return true;
        }
    }

    /// <summary>Whether the page at <paramref name="address"/> in an allocation committed in part is committed.</summary>
    private static bool CommittedPage(nint address)
    {
        MemoryBasicInformation info;

        return Kernel32.VirtualQueryEx(Kernel32.GetCurrentProcess(), address, &info, (nuint)sizeof(MemoryBasicInformation)) != 0
            && info.Type == PrivateMemory && info.State == Win32Constants.MemCommit;
    }

    /// <summary>Whether the game allocated <paramref name="allocation"/> as GPU-writable memory; logs the ones that are not.</summary>
    private bool GpuWritable(nint allocation, ulong size)
    {
        if (_graphics.TryGet(allocation, out var access, out uint type, out uint protect))
        {
            if (access == GraphicsAccess.ReadWrite)
            {
                return true;
            }

            _log.Warn($"[d3d12_xs] game memory 0x{allocation:x} (0x{size:x} bytes, type 0x{type:x}, protect 0x{protect:x}) is not GPU-writable graphics memory: GPU writes there are dropped");
        }
        else
        {
            _log.Warn($"[d3d12_xs] game memory 0x{allocation:x} (0x{size:x} bytes) was not allocated through XMemVirtualAlloc: GPU writes there are dropped");
        }

        return false;
    }

    /// <summary>The bytes from <paramref name="allocation"/> to the end of the last region with that allocation base.</summary>
    private static ulong AllocationSize(nint allocation)
    {
        MemoryBasicInformation info;
        nint next = allocation;

        while (Kernel32.VirtualQueryEx(Kernel32.GetCurrentProcess(), next, &info, (nuint)sizeof(MemoryBasicInformation)) != 0 && info.AllocationBase == allocation)
        {
            next = info.BaseAddress + (nint)info.RegionSize;
        }

        return (ulong)(next - allocation);
    }

    private nint CreateBuffer(nint device, nint allocation, ulong size, bool writable)
    {
        Guid device3Iid = Device3Iid;
        nint device3 = 0;
        int hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)ComObject.Slot(device, ComObject.QueryInterfaceSlot))(device, &device3Iid, &device3);

        if (HResult.Failed(hr))
        {
            return 0;
        }

        Guid heapIid = HeapIid;
        nint heap = 0;
        hr = ((delegate* unmanaged<nint, nint, Guid*, nint*, int>)ComObject.Slot(device3, OpenHeapFromAddressSlot))(device3, allocation, &heapIid, &heap);
        ComObject.Release(device3);

        if (HResult.Failed(hr))
        {
            _log.Warn($"[d3d12_xs] game memory 0x{allocation:x}: OpenExistingHeapFromAddress failed: {HResult.Format(hr)}");

            return 0;
        }

        int flags = writable ? CrossAdapter | UnorderedAccess : CrossAdapter;
        var description = new ResourceDescription { Dimension = ResourceDescription.BufferDimension, Width = size, Height = 1, DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1, Layout = RowMajor, Flags = flags };
        Guid resourceIid = ResourceIid;
        nint buffer = 0;
        var create = (delegate* unmanaged<nint, nint, ulong, ResourceDescription*, int, void*, Guid*, nint*, int>)ComObject.Slot(device, CreatePlacedResourceSlot);
        hr = create(device, heap, 0, &description, CommonState, null, &resourceIid, &buffer);

        if (HResult.Failed(hr))
        {
            _log.Warn($"[d3d12_xs] game memory 0x{allocation:x}: no buffer of 0x{size:x} bytes: {HResult.Format(hr)}");
        }

        return buffer;
    }
}
