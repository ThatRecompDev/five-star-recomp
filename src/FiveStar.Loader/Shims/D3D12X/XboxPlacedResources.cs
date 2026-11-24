// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Resources;
using FiveStar.Loader.Shims.D3D12X.States;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// Device slot 133: a buffer at a GPU address the game chose, over XMem pages it mapped itself. The store's view of
/// each page section is opened as a heap (ID3D12Device3::OpenExistingHeapFromAddress), so the GPU reads and writes the
/// same memory as the game's CPU views. Each run of pages that is contiguous in one section becomes a buffer placed at
/// the run's offset; the game's resource object is the first run's buffer, and every run's console and desktop
/// addresses go into <see cref="Addresses"/>. A range with no mapped page in memory the game committed itself shares that memory's buffer;
/// elsewhere it gets a committed buffer that is not shared.
/// </summary>
internal sealed unsafe class XboxPlacedResources
{
    /// <summary>ID3D12Device slots in desktop order and ID3D12Device3::OpenExistingHeapFromAddress (d3d12.h).</summary>
    private const int DesktopCreateCommittedResourceSlot = 27;
    private const int DesktopCreatePlacedResourceSlot = 29;
    private const int OpenHeapFromAddressSlot = 48;

    /// <summary>ID3D12Resource::GetGPUVirtualAddress (d3d12.h).</summary>
    private const int GetGpuAddressSlot = 11;
    private const int GetDescSlot = 10;

    /// <summary>IUnknown::AddRef.</summary>
    private const int AddRefSlot = 1;

    /// <summary>
    /// D3D12_RESOURCE_STATE_COMMON, D3D12_HEAP_FLAG_NONE, D3D12_HEAP_TYPE_DEFAULT and
    /// D3D12_RESOURCE_FLAG_ALLOW_CROSS_ADAPTER, which a resource on an opened (shared, cross-adapter) heap needs (d3d12.h).
    /// </summary>
    private const int CommonState = 0;
    private const int NoHeapFlags = 0;
    private const int DefaultHeapType = 1;
    private const int AllowCrossAdapter = 0x10;

    /// <summary>D3D12_TEXTURE_LAYOUT_ROW_MAJOR, the layout of every buffer (d3d12.h).</summary>
    private const int RowMajorLayout = 1;

    /// <summary>Bytes of section one heap opens at most: OpenExistingHeapFromAddress refuses the view of a 6 GiB section.</summary>
    private const ulong HeapWindowBytes = 1UL << 30;

    /// <summary>The most pages one read-only run over mapped pages takes (<see cref="CoverMappedPages"/>).</summary>
    private const ulong MaxCoveredPages = 64;

    /// <summary>D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, the one desktop flag the game's buffers carry (d3d12.h).</summary>
    private const int AllowUnorderedAccess = 0x4;

    private static readonly Guid Device3Iid = new("81dadc15-2bad-4392-93c5-101345c4aa98");
    private static readonly Guid HeapIid = new("6b3b2502-6e51-45b3-90ee-9884265e8df3");
    private static readonly Guid ResourceIid = new("696442be-a72e-4059-bc79-5b5c98040fad");

    private readonly object _gate = new();
    private readonly Dictionary<nint, nint> _heaps = [];
    private readonly Dictionary<(nint Section, ulong Window), nint> _windowHeaps = [];
    private readonly List<nint> _runBuffers = [];
    private readonly XMemPhysicalMemory _memory;
    private readonly XboxObjects _objects;
    private readonly GpuTextures _textures;
    private readonly LoaderLog _log;

    public XboxPlacedResources(XMemPhysicalMemory memory, XboxObjects objects, GpuTextures textures, LoaderLog log)
    {
        _memory = memory;
        _objects = objects;
        _textures = textures;
        _log = log;
    }

    /// <summary>Console GPU address to desktop GPU address, for every buffer made here.</summary>
    public AddressTable Addresses { get; } = new();

    /// <summary>The buffers over memory the game committed itself; set once both exist.</summary>
    public XboxGameMemoryBuffers? GameMemory { get; set; }

    /// <summary>Creates the buffers over the game's pages at <paramref name="address"/> and returns the first wrapped.</summary>
    public int Create(nint device, ulong address, ResourceDescription* description, int initialLayout, void* clear, nint* resource)
    {
        *resource = 0;

        if (description->Dimension != ResourceDescription.BufferDimension)
        {
            return CreateTexture(device, address, description, initialLayout, clear, resource);
        }

        lock (_gate)
        {
            var runs = Runs(address, description->Width);
            nint inner = PlaceRuns(device, address, description, runs);

            if (inner == 0)
            {
                inner = InGameMemory(address, description->Width, (description->Flags & AllowUnorderedAccess) != 0);
            }

            if (inner == 0)
            {
                inner = CreateCommitted(device, address, description);
            }

            if (inner == 0)
            {
                _log.Error($"  [d3d12_xs] ID3D12Device.X::Slot133 at 0x{address:x}: no desktop buffer of 0x{description->Width:x} bytes");

                return HResult.Fail;
            }

            *resource = _objects.WrapResource(inner, address);
            int flags = GameMemory?.IsReadOnly(inner) == true ? 0 : AllowUnorderedAccess;
            ResourceStates.Record(_objects.AsResource(*resource), ResourceStates.BufferDimension, ResourceStates.Common, flags);
            _log.Detail($"  [d3d12_xs] ID3D12Device.X::Slot133 CreatePlacedResource(0x{address:x}, 0x{description->Width:x} bytes) -> {runs.Count} page runs");

            return HResult.Ok;
        }
    }

    /// <summary>
    /// A texture at a game address. A desktop texture with the driver's layout cannot sit on a heap over game memory,
    /// so it is a committed texture of its own, registered by address and filled from the game's bytes unless the GPU
    /// writes it (<see cref="GpuTextures"/>). GetGPUVirtualAddress still answers the game's address.
    /// </summary>
    private int CreateTexture(nint device, ulong address, ResourceDescription* description, int initialLayout, void* clear, nint* resource)
    {
        int hr = _textures.CreatePlaced(device, address, description, initialLayout, clear, out nint inner, out int state);
        _log.Detail($"  [d3d12_xs] ID3D12Device.X::Slot133 texture at 0x{address:x}: dimension {description->Dimension}, {description->Width}x{description->Height}, format {description->Format}, layout {initialLayout} -> {HResult.Format(hr)}");

        if (HResult.Failed(hr))
        {
            return _objects.WrapResult(hr, XboxInterfaceKind.Resource, resource);
        }

        *resource = _objects.WrapResource(inner, address);
        ResourceStates.Record(_objects.AsResource(*resource), description->Dimension, state, DesktopFlags(inner));

        return hr;
    }

    /// <summary>One placed buffer per mapped run; returns the first, keeps the others alive.</summary>
    private nint PlaceRuns(nint device, ulong address, ResourceDescription* description, List<PageRun> runs)
    {
        nint first = 0;

        foreach (var run in runs)
        {
            if (run.Chunk is null)
            {
                continue;
            }

            _log.Detail($"  [d3d12_xs]   run +0x{run.Start:x} 0x{run.Length:x} bytes at section offset 0x{run.Offset:x} (section 0x{run.Chunk.Section:x})");
            var desktop = DesktopBuffer(description);
            desktop.Width = run.Length;
            desktop.Flags |= AllowCrossAdapter;
            nint buffer = CreatePlaced(device, run, &desktop);

            if (buffer == 0)
            {
                continue;
            }

            Addresses.Add(new AddressRange(address + run.Start, run.Length, GpuAddress(buffer), buffer));

            if (first == 0)
            {
                first = buffer;
            }
            else
            {
                _runBuffers.Add(buffer);
            }
        }

        return first;
    }

    /// <summary>Splits the range into runs of pages that are consecutive in one section (or unmapped).</summary>
    private List<PageRun> Runs(ulong address, ulong size)
    {
        var runs = new List<PageRun>();
        ulong pageSize = XMemPhysicalMemory.PageSize;

        for (ulong start = 0; start < size; start += pageSize)
        {
            _memory.TryLocate((nint)(address + start), out var chunk, out ulong offset);
            ulong length = Math.Min(pageSize, size - start);

            if (runs.Count > 0 && Continues(runs[^1], chunk, offset))
            {
                runs[^1] = runs[^1] with { Length = runs[^1].Length + length };
            }
            else
            {
                runs.Add(new PageRun(chunk, offset, start, length));
            }
        }

        return runs;
    }

    private static bool Continues(PageRun run, XMemPageChunk? chunk, ulong offset)
    {
        if (run.Chunk is null || chunk is null)
        {
            return run.Chunk is null && chunk is null;
        }

        return ReferenceEquals(run.Chunk, chunk) && run.Offset + run.Length == offset;
    }

    /// <summary>
    /// The game's buffer desc with what the desktop rejects removed: its flags are 0x20004, UNORDERED_ACCESS plus an
    /// X-only bit 0x20000 (desc built at 0x141e867e7), and the alignment is left to the desktop.
    /// </summary>
    private static ResourceDescription DesktopBuffer(ResourceDescription* description)
    {
        var desktop = *description;
        desktop.Alignment = 0;
        desktop.Flags &= AllowUnorderedAccess;

        return desktop;
    }

    /// <summary>The desktop resource's D3D12_RESOURCE_FLAGS (ID3D12Resource::GetDesc).</summary>
    private static int DesktopFlags(nint resource)
    {
        ResourceDescription desc;
        _ = ((delegate* unmanaged<nint, ResourceDescription*, ResourceDescription*>)ComObject.Slot(resource, GetDescSlot))(resource, &desc);

        return desc.Flags;
    }

    private static ulong GpuAddress(nint resource)
    {
        return ((delegate* unmanaged<nint, ulong>)ComObject.Slot(resource, GetGpuAddressSlot))(resource);
    }

    private nint CreatePlaced(nint device, PageRun run, ResourceDescription* description)
    {
        nint heap = Heap(device, run.Chunk!);
        ulong offset = run.Offset;

        if (heap == 0)
        {
            heap = WindowHeap(device, run, out offset);
        }

        if (heap == 0)
        {
            return 0;
        }

        Guid iid = ResourceIid;
        nint resource = 0;
        var create = (delegate* unmanaged<nint, nint, ulong, ResourceDescription*, int, void*, Guid*, nint*, int>)ComObject.Slot(device, DesktopCreatePlacedResourceSlot);
        int hr = create(device, heap, offset, description, CommonState, null, &iid, &resource);
        LogFailure("CreatePlacedResource", hr);

        return resource;
    }

    /// <summary>
    /// A buffer in memory the game committed itself (no XMem page): the desktop buffer over that whole allocation,
    /// referenced once more, so the GPU sees the game's writes. Its offsets are console ones; views and copies move
    /// them (<see cref="Resources.BufferViewRanges"/>, <see cref="BufferCopies"/>). 0 when no such buffer covers it, or
    /// when the game wants unordered access in memory it did not allocate as GPU-writable.
    /// </summary>
    private nint InGameMemory(ulong address, ulong size, bool unordered)
    {
        if (GameMemory is null || !GameMemory.TryFind(address, out var range) || range.Resource == 0 || address + size > range.GameAddress + range.Size)
        {
            return 0;
        }

        if (unordered && GameMemory.IsReadOnly(range.Resource))
        {
            _log.Warn($"[d3d12_xs] ID3D12Device.X::Slot133 at 0x{address:x}: a UAV buffer in memory not allocated as GPU-writable");

            return 0;
        }

        ((delegate* unmanaged<nint, uint>)ComObject.Slot(range.Resource, AddRefSlot))(range.Resource);

        return range.Resource;
    }

    private nint CreateCommitted(nint device, ulong address, ResourceDescription* description)
    {
        var desktop = DesktopBuffer(description);

        int* properties = stackalloc int[] { DefaultHeapType, 0, 0, 0, 0 };
        Guid iid = ResourceIid;
        nint resource = 0;
        var create = (delegate* unmanaged<nint, int*, int, ResourceDescription*, int, void*, Guid*, nint*, int>)ComObject.Slot(device, DesktopCreateCommittedResourceSlot);
        int hr = create(device, properties, NoHeapFlags, &desktop, CommonState, null, &iid, &resource);
        LogFailure("CreateCommittedResource", hr);

        if (resource != 0)
        {
            _log.Warn($"[d3d12_xs] ID3D12Device.X::Slot133 at 0x{address:x}: no mapped page, committed buffer of 0x{description->Width:x} bytes (not shared with the game)");
            Addresses.Add(new AddressRange(address, description->Width, GpuAddress(resource), resource));
        }

        return resource;
    }

    /// <summary>
    /// A read-only buffer over the XMem pages mapped around <paramref name="address"/> when no buffer covers it: memory
    /// the game reads through the GPU without a placed resource there, such as constant buffers in its pools (on the
    /// console the GPU reads any mapped page). The run takes the pages consecutive in the same section, up to
    /// <see cref="MaxCoveredPages"/>, that no other range overlaps; a later placed resource there replaces it. Without
    /// unordered access the address map gives programs no UAV over it. False when nothing is mapped there or a range
    /// already overlaps the page.
    /// </summary>
    public bool CoverMappedPages(nint device, ulong address)
    {
        ulong pageSize = XMemPhysicalMemory.PageSize;
        ulong page = address & ~(pageSize - 1);

        lock (_gate)
        {
            if (!_memory.TryLocate((nint)page, out var chunk, out ulong offset) || Addresses.Overlaps(page, pageSize))
            {
                return false;
            }

            ulong start = page;
            ulong end = page + pageSize;

            // A run stays inside one heap window of its section (WindowHeap).
            ulong windowStart = offset / HeapWindowBytes * HeapWindowBytes;

            while (end - start < MaxCoveredPages * pageSize && offset + (end - page) + pageSize <= windowStart + HeapWindowBytes
                && ContinuesRun(end, chunk, offset + (end - page)))
            {
                end += pageSize;
            }

            while (end - start < MaxCoveredPages * pageSize && start >= pageSize && offset >= windowStart + (page - start) + pageSize
                && ContinuesRun(start - pageSize, chunk, offset - (page - start) - pageSize))
            {
                start -= pageSize;
            }

            var run = new PageRun(chunk, offset - (page - start), 0, end - start);
            var description = new ResourceDescription
            {
                Dimension = ResourceDescription.BufferDimension, Width = run.Length, Height = 1, DepthOrArraySize = 1, MipLevels = 1,
                SampleCount = 1, Layout = RowMajorLayout, Flags = AllowCrossAdapter,
            };
            nint buffer = CreatePlaced(device, run, &description);

            if (buffer == 0)
            {
                return false;
            }

            Addresses.Add(new AddressRange(start, run.Length, GpuAddress(buffer), buffer));
            ComObject.Release(buffer);
            _log.Detail($"  [d3d12_xs] mapped pages 0x{start:x} (0x{run.Length:x} bytes, section offset 0x{run.Offset:x}) on the GPU read-only");

            return true;
        }
    }

    /// <summary>Whether the page at <paramref name="page"/> is mapped from <paramref name="chunk"/> at <paramref name="offset"/> and no range overlaps it.</summary>
    private bool ContinuesRun(ulong page, XMemPageChunk chunk, ulong offset)
    {
        return _memory.TryLocate((nint)page, out var next, out ulong at) && ReferenceEquals(next, chunk) && at == offset
            && !Addresses.Overlaps(page, XMemPhysicalMemory.PageSize);
    }

    /// <summary>
    /// A heap over the <see cref="HeapWindowBytes"/> window of the run's section that holds the run, through a view of
    /// that window: the store makes one section per contiguous allocation, and its whole-section view of a 6 GiB one is
    /// refused (E_INVALIDARG). <paramref name="offset"/> is the run's offset in that heap; 0 when the run crosses a
    /// window's end or the view cannot be made.
    /// </summary>
    private nint WindowHeap(nint device, PageRun run, out ulong offset)
    {
        var chunk = run.Chunk!;
        ulong window = run.Offset / HeapWindowBytes;
        ulong start = window * HeapWindowBytes;
        ulong bytes = Math.Min(HeapWindowBytes, (chunk.Pages * XMemPhysicalMemory.PageSize) - start);
        offset = run.Offset - start;

        if (offset + run.Length > bytes)
        {
            return 0;
        }

        if (_windowHeaps.TryGetValue((chunk.Section, window), out nint existing))
        {
            return existing;
        }

        nint view = KernelBase.MapViewOfFile3(chunk.Section, Kernel32.GetCurrentProcess(), 0, start, (nuint)bytes, 0, Win32Constants.PageReadWrite, null, 0);
        nint heap = 0;

        if (view != 0)
        {
            heap = OpenHeap(device, view);
        }

        _windowHeaps[(chunk.Section, window)] = heap;
        _log.Detail($"  [d3d12_xs] heap over section 0x{chunk.Section:x} window {window} (0x{bytes:x} bytes at 0x{view:x}) -> 0x{heap:x}");

        return heap;
    }

    private nint OpenHeap(nint device, nint view)
    {
        Guid device3Iid = Device3Iid;
        nint device3 = 0;
        int hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)ComObject.Slot(device, ComObject.QueryInterfaceSlot))(device, &device3Iid, &device3);

        if (HResult.Failed(hr))
        {
            LogFailure("QueryInterface(ID3D12Device3)", hr);

            return 0;
        }

        Guid heapIid = HeapIid;
        nint heap = 0;
        hr = ((delegate* unmanaged<nint, nint, Guid*, nint*, int>)ComObject.Slot(device3, OpenHeapFromAddressSlot))(device3, view, &heapIid, &heap);
        ComObject.Release(device3);
        LogFailure($"OpenExistingHeapFromAddress(0x{view:x})", hr);

        return heap;
    }

    /// <summary>
    /// The heap over a page section (through the store's whole-section view), opened once; 0 for a section larger than
    /// <see cref="HeapWindowBytes"/>, which takes heaps by window (<see cref="WindowHeap"/>).
    /// </summary>
    private nint Heap(nint device, XMemPageChunk chunk)
    {
        if (chunk.Pages * XMemPhysicalMemory.PageSize > HeapWindowBytes)
        {
            return 0;
        }

        if (_heaps.TryGetValue(chunk.Section, out nint existing))
        {
            return existing;
        }

        nint heap = OpenHeap(device, chunk.Window);
        _heaps[chunk.Section] = heap;

        return heap;
    }

    private void LogFailure(string call, int hr)
    {
        if (HResult.Failed(hr))
        {
            _log.Warn($"[d3d12_xs] {call} failed: {HResult.Format(hr)}");
        }
    }
}
