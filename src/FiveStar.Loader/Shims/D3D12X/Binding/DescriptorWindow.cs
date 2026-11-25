// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Diagnostics;

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// The GPU-memory window of the translated programs (heap slots 0 and 1, docs/shader-translation.md, Resources) as far
/// as descriptor tables need it. A program reads a table through a 32-bit pointer in a user SGPR: descriptor k of the
/// table at pointer + k * 32 (T# size; buffers and samplers use the first 16 bytes), and dword 0 of that descriptor is
/// the desktop heap index. The window holds one 16-byte record per sampler of the game's sampler heap, a null table,
/// then one 32-byte record per descriptor of the game's view heap, in game order; <see cref="ShaderVisibleHeap"/>
/// keeps dword 0 of each record equal to the desktop index the game's descriptor stands for.
/// </summary>
internal sealed unsafe class DescriptorWindow
{
    /// <summary>D3D12_MAX_SHADER_VISIBLE_DESCRIPTOR_HEAP_SIZE_TIER_2 and D3D12_MAX_SHADER_VISIBLE_SAMPLER_HEAP_SIZE.</summary>
    public const uint MaxViews = 1000000;
    public const uint MaxSamplers = 2048;

    /// <summary>Bytes of a view record (a T#) and a sampler record (an S#) in the window.</summary>
    public const uint ViewRecordBytes = 32;
    public const uint SamplerRecordBytes = 16;

    /// <summary>
    /// Records mirrored for a table of unbounded or unknown size: the game's per-draw tables place descriptors at
    /// byte offsets below 0x8000 (the 0x7fe0 mask at 0x144d9f04f), 1024 records.
    /// </summary>
    public const uint MaxTableRecords = 1024;

    /// <summary>
    /// Records mirrored for any table, whatever its root-signature ranges say: the game places a table's descriptors
    /// at the offsets of its own binding entries (0x144d9efc0), and its programs read them there (default_ps_tex reads
    /// its T# at record 1 of a table whose root signature spans one descriptor).
    /// </summary>
    public const uint MinTableRecords = 16;

    /// <summary>Heap slots the programs reach by constant: the window (rdna_prelude GPU_MEM_HEAP), its writable view, the mesh fallback table.</summary>
    public const uint WindowSlot = 0;
    public const uint WritableWindowSlot = 1;
    public const uint FallbackTableSlot = 2;

    /// <summary>The fallback table's SRV, a null descriptor nothing rewrites: what a record naming no descriptor reads.</summary>
    public const uint NullViewSlot = FallbackTableSlot + FallbackUavs;

    /// <summary>Where views go that no desktop descriptor is left for.</summary>
    public const uint SinkSlot = FallbackTableSlot + FallbackTableSize;

    /// <summary>rdna_prelude GPU_MAP_HEAP: the address map of game memory (<see cref="GameMemoryViews"/>).</summary>
    public const uint AddressMapSlot = FiveStar.Shaders.Translation.AddressMap.HeapSlot;

    /// <summary>The null SRV and UAV (next slot) that unmapped pages of the address map name.</summary>
    public const uint UnmappedSlot = AddressMapSlot + 1;

    /// <summary>The first desktop index of the game's descriptors.</summary>
    public const uint ReservedViews = UnmappedSlot + 2;

    /// <summary>rdna_prelude STATIC_SAMPLER_BASE: samplers for the literal S#s of a program.</summary>
    public const uint StaticSamplerBase = 2032;

    /// <summary>Window layout: sampler records, a null table (for a table handle outside the heaps), view records.</summary>
    public const uint SamplerBase = 0;
    public const uint NullTableBase = SamplerBase + (MaxSamplers * SamplerRecordBytes);
    public const uint ViewBase = NullTableBase + (MaxTableRecords * ViewRecordBytes);

    /// <summary>The mesh fallback table: u0..u2 then t0 (BindlessRootSignature).</summary>
    private const uint FallbackTableSize = 4;
    private const uint FallbackUavs = 3;
    private const int CommonState = 0;

    private readonly LoaderLog _log;
    private ulong _viewGpuStart;
    private uint _viewIncrement;

    public DescriptorWindow(LoaderLog log)
    {
        _log = log;
    }

    /// <summary>The desktop shader-visible view heap, once the game created its heap.</summary>
    public nint ViewHeap { get; private set; }

    /// <summary>The desktop shader-visible sampler heap, once the game created its heap.</summary>
    public nint SamplerHeap { get; private set; }

    /// <summary>The GPU handle of the mesh fallback table (root parameter 1 of every translated program).</summary>
    public ulong FallbackTable => _viewGpuStart + ((ulong)FallbackTableSlot * _viewIncrement);

    /// <summary>The window buffer; 0 until the view heap exists.</summary>
    public nint Buffer { get; private set; }

    /// <summary>The window's CPU mapping; null until the view heap exists.</summary>
    public byte* Data { get; private set; }

    /// <summary>The window flags of <see cref="WindowFeedback"/>, indexed by window id; null without it or a window.</summary>
    public uint* WindowFlags
    {
        get
        {
            if (!WindowFeedback.Enabled || Data == null)
            {
                return null;
            }

            return (uint*)(Data + WindowFeedback.Base);
        }
    }

    /// <summary>
    /// Sets up the window for the game's shader-visible view heap of <paramref name="views"/> descriptors: the window
    /// buffer (CPU-writable, GPU-readable and writable), its two views, the null descriptors and the records.
    /// </summary>
    public bool AttachViews(nint device, nint heap, uint views, uint increment)
    {
        uint records = ViewBase + (views * ViewRecordBytes);
        uint bytes = records;

        // --trace-pool-stores: the store ring past the most view records the window can hold.
        if (DebugLayer.PoolStoreTrace.Enabled && bytes < DebugLayer.PoolStoreTrace.Bytes)
        {
            bytes = DebugLayer.PoolStoreTrace.Bytes;
        }

        // --evict-windows: the flags of the windows programs ask for, past the store-trace ring.
        if (WindowFeedback.Enabled && bytes < WindowFeedback.Bytes)
        {
            bytes = WindowFeedback.Bytes;
        }

        // --probe-pipeline: the mesh probe's ring, past the flags.
        if (DebugLayer.MeshProbes.Enabled && bytes < DebugLayer.MeshProbes.Bytes)
        {
            bytes = DebugLayer.MeshProbes.Bytes;
        }

        var properties = new HeapProperties { Type = HeapProperties.Custom, CpuPageProperty = HeapProperties.WriteCombine, MemoryPool = HeapProperties.SystemPool };
        nint buffer = DesktopCalls.CreateBuffer(device, properties, bytes, DesktopCalls.UnorderedAccessFlag, CommonState);
        byte* data = buffer == 0 ? null : DesktopCalls.Map(buffer);

        if (data == null)
        {
            _log.Warn("[d3d12_xs] descriptor window: no CPU-writable buffer, descriptor tables read nothing");

            return false;
        }

        FillRecords(data, bytes, records);
        WriteReserved(device, buffer, bytes, DesktopCalls.HeapCpuStart(heap), increment);

        Buffer = buffer;
        Data = data;
        ViewHeap = heap;
        _viewGpuStart = DesktopCalls.HeapGpuStart(heap);
        _viewIncrement = increment;
        _log.Detail($"  [d3d12_xs] descriptor window: 0x{bytes:x} bytes, view records from 0x{ViewBase:x}, game views from desktop index {ReservedViews}");

        return true;
    }

    /// <summary>Takes the game's shader-visible sampler heap and fills the static sampler slots with a trilinear sampler.</summary>
    public void AttachSamplers(nint device, nint heap, uint count, uint increment)
    {
        uint* sampler = stackalloc uint[TrilinearSampler.Dwords];
        TrilinearSampler.Write(sampler);
        nint cpu = DesktopCalls.HeapCpuStart(heap);

        for (uint slot = StaticSamplerBase; slot < count; slot++)
        {
            DesktopCalls.Sampler(device, sampler, cpu + (nint)(slot * increment));
        }

        SamplerHeap = heap;
    }

    /// <summary>Window records of the game's view heap: game order from <see cref="ViewBase"/>.</summary>
    public static HeapRecords ViewRecords(uint desktopCount)
    {
        return new HeapRecords(ViewBase, ViewRecordBytes, ReservedViews, desktopCount, NullViewSlot);
    }

    /// <summary>Window records of the game's sampler heap: the game's indices are the desktop ones.</summary>
    public static HeapRecords SamplerRecords(uint desktopCount)
    {
        return new HeapRecords(SamplerBase, SamplerRecordBytes, 0, Math.Min(desktopCount, MaxSamplers), 0);
    }

    /// <summary>Null view records everywhere, sampler records naming their own index.</summary>
    private static void FillRecords(byte* data, uint bytes, uint records)
    {
        new Span<byte>(data, (int)bytes).Clear();

        for (uint sampler = 0; sampler < MaxSamplers; sampler++)
        {
            *(uint*)(data + SamplerBase + (sampler * SamplerRecordBytes)) = sampler;
        }

        for (uint offset = NullTableBase; offset < records; offset += ViewRecordBytes)
        {
            *(uint*)(data + offset) = NullViewSlot;
        }
    }

    private static void WriteReserved(nint device, nint buffer, uint bytes, nint cpu, uint increment)
    {
        var read = BufferViewDescription.RawShaderResource(bytes / sizeof(uint));
        var write = BufferViewDescription.RawUnorderedAccess(bytes / sizeof(uint));
        DesktopCalls.ShaderResourceView(device, buffer, &read, cpu + (nint)(WindowSlot * increment));
        DesktopCalls.UnorderedAccessView(device, buffer, &write, cpu + (nint)(WritableWindowSlot * increment));

        // Null descriptors: the fallback table the root signature asks for, the sink, the address map until it exists
        // and the pair unmapped pages name.
        var nullWrite = BufferViewDescription.RawUnorderedAccess(0);
        var nullRead = BufferViewDescription.RawShaderResource(0);

        for (uint slot = FallbackTableSlot; slot < FallbackTableSlot + FallbackUavs; slot++)
        {
            DesktopCalls.UnorderedAccessView(device, 0, &nullWrite, cpu + (nint)(slot * increment));
        }

        for (uint slot = NullViewSlot; slot <= UnmappedSlot; slot++)
        {
            DesktopCalls.ShaderResourceView(device, 0, &nullRead, cpu + (nint)(slot * increment));
        }

        DesktopCalls.UnorderedAccessView(device, 0, &nullWrite, cpu + (nint)((UnmappedSlot + 1) * increment));
    }
}
