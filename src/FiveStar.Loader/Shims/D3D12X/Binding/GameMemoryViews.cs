// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Pipelines;
using FiveStar.Shaders.Translation;

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// The address map through which translated programs read game memory at console GPU addresses (root descriptors,
/// V#s and pointers the game builds; rdna_prelude <c>gpu_page</c>, <see cref="AddressMap"/>). Every desktop buffer
/// the address table gains gets a raw SRV and UAV per 256 MiB of it, each view covering up to 512 MiB (the most a
/// raw view's element count reaches), and the map names for each 64 KiB page the views of its chunk. XMem pages follow
/// the page tables as the console GPU's do: every map, remap or unmap (<see cref="XMemPhysicalMemory.ObservePages"/>)
/// points the page where the GPU addresses it (its GPU-only alias when it has one, else its CPU address) into views
/// over the section that holds its frame now, chunk by chunk as above, or at the null views. Buffers placed over pages
/// keep their frames; the map does not. The map lives in write-combined system memory read at heap slot
/// <see cref="DescriptorWindow.AddressMapSlot"/>; the views take whole remap pages of the desktop heap. Ranges and
/// pages mapped before the game's view heap exists wait for it.
/// </summary>
internal sealed unsafe class GameMemoryViews
{
    /// <summary>4 GiB spans the map can hold: the boot's game memory spans about 20.</summary>
    private const int MapSpans = 64;

    /// <summary>Remap pages for the views: 16,384 descriptors, 8,192 SRV/UAV pairs.</summary>
    private const uint DescriptorPages = 4;

    private const ulong ChunkBytes = 256UL << 20;
    private const ulong ViewBytes = 512UL << 20;
    private const uint DwordBytes = 4;
    private const uint PairSize = 2;
    private const int CommonState = 0;

    /// <summary>ID3D12Resource::GetDesc and D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS (d3d12.h).</summary>
    private const int GetDescSlot = 10;
    private const int AllowUnorderedAccess = 0x4;

    private const ulong PageMask = XMemPhysicalMemory.PageSize - 1;

    /// <summary>D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS | ALLOW_CROSS_ADAPTER, which a buffer on a section heap needs.</summary>
    private const int SectionBufferFlags = 0x14;

    private readonly object _gate = new();
    private readonly List<AddressRange> _pending = [];
    private readonly Dictionary<(nint Section, ulong Chunk), uint> _sectionViews = [];
    private readonly XMemPhysicalMemory _memory;
    private readonly SectionHeaps _heaps;
    private readonly AddressMap _map = new(MapSpans, DescriptorWindow.UnmappedSlot);
    private readonly OnceLog _warnings;
    private readonly LoaderLog _log;
    private nint _device;
    private nint _cpuStart;
    private uint _increment;
    private byte* _data;
    private uint _next;
    private uint _end;

    public GameMemoryViews(XMemPhysicalMemory memory, SectionHeaps heaps, OnceLog warnings, LoaderLog log)
    {
        _memory = memory;
        _heaps = heaps;
        _warnings = warnings;
        _log = log;
    }

    /// <summary>Maps the pages of a range the address table gained (<see cref="AddressTable.Observe"/>).</summary>
    public void Add(AddressRange range)
    {
        if (range.Resource == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_data == null)
            {
                _pending.Add(range);

                return;
            }

            Place(range);
        }
    }

    /// <summary>
    /// Creates the map for the desktop view heap at <paramref name="cpuStart"/>, takes its views' descriptors from
    /// <paramref name="remap"/> and maps the ranges added so far.
    /// </summary>
    public void Attach(nint device, nint cpuStart, uint increment, DescriptorRemap remap)
    {
        lock (_gate)
        {
            var properties = new HeapProperties { Type = HeapProperties.Custom, CpuPageProperty = HeapProperties.WriteCombine, MemoryPool = HeapProperties.SystemPool };
            nint buffer = DesktopCalls.CreateBuffer(device, properties, (ulong)_map.Bytes, 0, CommonState);
            byte* data = buffer == 0 ? null : DesktopCalls.Map(buffer);
            uint first = remap.Reserve(DescriptorPages);

            if (data == null || first == DescriptorRemap.Unmapped)
            {
                _log.Warn("[d3d12_xs] address map: no buffer or descriptors; programs read game memory at console addresses as zeros");

                return;
            }

            _map.Clear(new Span<byte>(data, _map.Bytes));
            var view = BufferViewDescription.RawShaderResource((uint)_map.Bytes / DwordBytes);
            DesktopCalls.ShaderResourceView(device, buffer, &view, cpuStart + (nint)(DescriptorWindow.AddressMapSlot * increment));

            _device = device;
            _cpuStart = cpuStart;
            _increment = increment;
            _data = data;

            _next = first;
            _end = first + (DescriptorPages * DescriptorRemap.PageSize);
            _log.Detail($"  [d3d12_xs] address map: 0x{_map.Bytes:x} bytes for {MapSpans} spans, views from desktop index {first}, {_pending.Count} ranges waiting");

            foreach (var range in _pending)
            {
                Place(range);
            }

            _pending.Clear();
            int pages = 0;
            _memory.VisitMapped((address, _) =>
            {
                Follow(address);
                pages++;
            });
            _log.Detail($"  [d3d12_xs] address map: {pages} mapped XMem pages follow their frames");
        }
    }

    /// <summary><see cref="XMemPhysicalMemory.ObservePages"/>: points the page at <paramref name="address"/> at its new frame.</summary>
    public void PageChanged(nint address, ulong previous, ulong frame)
    {
        lock (_gate)
        {
            if (_data != null)
            {
                Follow(address);
            }
        }
    }

    /// <summary>
    /// The map entry of the page the CPU maps at <paramref name="address"/>, where the GPU addresses it: the game maps
    /// pages only into the CPU range of a pair and gives the GPU the alias (docs/physical-memory.md).
    /// </summary>
    private void Follow(nint address)
    {
        nint page = (nint)((ulong)address & ~PageMask);
        nint alias = _memory.GpuAlias(page);

        if (alias != 0)
        {
            MapPage((ulong)alias);

            return;
        }

        MapPage((ulong)page);
    }

    /// <summary>One page's entry: the section view of the frame mapped there now, else the null views.</summary>
    private void MapPage(ulong page)
    {
        var map = new Span<byte>(_data, _map.Bytes);
        uint pair = DescriptorWindow.UnmappedSlot;
        ulong viewStart = page;

        if (_memory.TryLocate((nint)page, out var chunk, out ulong offset))
        {
            pair = SectionView(chunk, offset, out ulong inView);
            viewStart = page - inView;
        }

        if (pair == DescriptorWindow.UnmappedSlot)
        {
            viewStart = page;
        }

        if (!_map.Map(map, page, XMemPhysicalMemory.PageSize, pair, viewStart))
        {
            _warnings.Warn("address-map-spans", $"[d3d12_xs] address map: no page table left for 0x{page:x}; it reads as zeros");
        }
    }

    /// <summary>
    /// The view pair over the chunk of <paramref name="chunk"/>'s section that holds byte <paramref name="offset"/>, made
    /// on first use, and the byte's offset in it; the null views when none can be made.
    /// </summary>
    private uint SectionView(XMemPageChunk chunk, ulong offset, out ulong inView)
    {
        ulong index = offset / ChunkBytes;
        inView = offset - (index * ChunkBytes);

        if (_sectionViews.TryGetValue((chunk.Section, index), out uint pair))
        {
            return pair;
        }

        // A chunk starts inside one heap (a window of a large section, SectionHeaps), and its view ends with that heap.
        nint heap = _heaps.Heap(_device, chunk, index * ChunkBytes, out ulong heapOffset, out ulong heapBytes);
        ulong bytes = Math.Min(ViewBytes, heapBytes - heapOffset);
        nint buffer = heap == 0 ? 0 : DesktopCalls.CreatePlacedBuffer(_device, heap, heapOffset, bytes, SectionBufferFlags, CommonState);
        pair = DescriptorWindow.UnmappedSlot;

        if (buffer == 0 || _next + PairSize > _end)
        {
            _warnings.Warn("address-map-sections", $"[d3d12_xs] address map: no view over section 0x{chunk.Section:x}; its pages read as zeros");
        }
        else
        {
            // The buffer lives as long as the map: its views are never replaced.
            pair = _next;
            _next += PairSize;
            WriteViews(buffer, 0, bytes, pair, true);
        }

        _sectionViews[(chunk.Section, index)] = pair;

        return pair;
    }

    /// <summary>Views over each chunk of the range's buffer and the map entries of its pages.</summary>
    private void Place(AddressRange range)
    {
        bool writable = (DesktopFlags(range.Resource) & AllowUnorderedAccess) != 0;
        var map = new Span<byte>(_data, _map.Bytes);

        for (ulong start = 0; start < range.Size; start += ChunkBytes)
        {
            if (_next + PairSize > _end)
            {
                _warnings.Warn("address-map-views", "[d3d12_xs] address map: no descriptors left; later game memory reads as zeros");

                return;
            }

            uint pair = _next;
            _next += PairSize;
            WriteViews(range.Resource, start, Math.Min(ViewBytes, range.Size - start), pair, writable);

            if (!_map.Map(map, range.GameAddress + start, Math.Min(ChunkBytes, range.Size - start), pair, range.GameAddress + start))
            {
                _warnings.Warn("address-map-spans", $"[d3d12_xs] address map: no page table left for 0x{range.GameAddress + start:x}; it reads as zeros");
            }
        }
    }

    private void WriteViews(nint resource, ulong offset, ulong bytes, uint pair, bool writable)
    {
        uint dwords = (uint)(bytes / DwordBytes);
        var read = BufferViewDescription.RawShaderResource(dwords, offset / DwordBytes);
        var write = BufferViewDescription.RawUnorderedAccess(dwords, offset / DwordBytes);
        nint handle = _cpuStart + (nint)(pair * _increment);
        DesktopCalls.ShaderResourceView(_device, resource, &read, handle);

        if (!writable)
        {
            // An upload buffer (list slot 148) takes no UAV: writes there go nowhere.
            write = BufferViewDescription.RawUnorderedAccess(0);
            resource = 0;
        }

        DesktopCalls.UnorderedAccessView(_device, resource, &write, handle + (nint)_increment);
    }

    /// <summary>The desktop resource's D3D12_RESOURCE_FLAGS.</summary>
    private static int DesktopFlags(nint resource)
    {
        ResourceDescription desc;
        _ = ((delegate* unmanaged<nint, ResourceDescription*, ResourceDescription*>)ComObject.Slot(resource, GetDescSlot))(resource, &desc);

        return desc.Flags;
    }
}
