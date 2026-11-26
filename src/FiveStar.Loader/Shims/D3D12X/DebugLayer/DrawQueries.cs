// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;
using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// The hardware's own counts around each draw and ExecuteIndirect the draw trace logs: a PIPELINE_STATISTICS1 query
/// (amplification and mesh invocations, mesh primitives, primitives into and out of the clipper, pixel shader
/// invocations, vertex and compute work) and an OCCLUSION query (samples through the depth and stencil tests), reported
/// with the probes two presents later. Together they name the stage where a pass's pixels are lost: no primitive out
/// of the mesh shader, none into the clipper, no pixel shader run, or no sample through depth and stencil.
/// </summary>
internal sealed unsafe class DrawQueries
{
    /// <summary>ID3D12GraphicsCommandList::BeginQuery, ::EndQuery, ::ResolveQueryData (d3d12.h).</summary>
    private const int BeginQuerySlot = 52;
    private const int EndQuerySlot = 53;
    private const int ResolveQueryDataSlot = 54;

    /// <summary>D3D12_QUERY_HEAP_TYPE_OCCLUSION and _PIPELINE_STATISTICS1; D3D12_QUERY_TYPE_OCCLUSION and _PIPELINE_STATISTICS1.</summary>
    private const int OcclusionHeap = 0;
    private const int StatisticsHeap = 7;
    private const int OcclusionQuery = 0;
    private const int StatisticsQuery = 10;

    /// <summary>D3D12_QUERY_DATA_PIPELINE_STATISTICS1: 14 counters, then the occlusion count resolved after them.</summary>
    public const int StatisticsCounters = 14;
    public const int ResultCounters = StatisticsCounters + 1;
    private const int StatisticsBytes = StatisticsCounters * sizeof(ulong);
    private const int ResultBytes = ResultCounters * sizeof(ulong);

    /// <summary>Counter indices in D3D12_QUERY_DATA_PIPELINE_STATISTICS1.</summary>
    private const int InputPrimitives = 1;
    private const int VertexInvocations = 2;
    private const int ClipperInvocations = 5;
    private const int ClipperPrimitives = 6;
    private const int PixelInvocations = 7;
    private const int ComputeInvocations = 10;
    private const int AmplificationInvocations = 11;
    private const int MeshInvocations = 12;
    private const int MeshPrimitives = 13;
    private const int Samples = StatisticsCounters;

    private readonly Dictionary<nint, Open> _open = [];
    private readonly List<Open> _ended = [];
    private readonly object _gate = new();
    private readonly LoaderLog _log;
    private bool _unavailable;

    public DrawQueries(LoaderLog log)
    {
        _log = log;
    }

    /// <summary>The counts as one line: mesh, clipper, pixel, vertex and compute work, then the samples through depth and stencil.</summary>
    public static string Describe(ReadOnlySpan<ulong> counts)
    {
        return $"AS {counts[AmplificationInvocations]} MS {counts[MeshInvocations]} MS primitives {counts[MeshPrimitives]}, "
            + $"clipper in {counts[ClipperInvocations]} out {counts[ClipperPrimitives]}, PS {counts[PixelInvocations]}, "
            + $"VS {counts[VertexInvocations]} IA primitives {counts[InputPrimitives]}, CS {counts[ComputeInvocations]}; "
            + $"samples through depth and stencil {counts[Samples]}";
    }

    /// <summary>Begins both queries on the desktop list <paramref name="list"/> before the traced work of the game's list <paramref name="key"/>.</summary>
    public void Begin(nint device, nint list, nint key, string label)
    {
        if (_unavailable)
        {
            return;
        }

        Open open;

        try
        {
            Marshal.AddRef(device);
            using var desktop = new ID3D12Device(device);
            var statistics = desktop.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription((QueryHeapType)StatisticsHeap, 1));
            var occlusion = desktop.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription((QueryHeapType)OcclusionHeap, 1));
            var buffer = desktop.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None, Vortice.Direct3D12.ResourceDescription.Buffer(ResultBytes), ResourceStates.CopyDest);
            open = new Open(label, statistics, occlusion, buffer);
        }
        catch (SharpGen.Runtime.SharpGenException exception)
        {
            _unavailable = true;
            _log.Info($"  [draws] pipeline statistics queries unavailable: {exception.Message}");

            return;
        }

        var begin = (delegate* unmanaged<nint, nint, int, uint, void>)ComObject.Slot(list, BeginQuerySlot);
        begin(list, open.Statistics.NativePointer, StatisticsQuery, 0);
        begin(list, open.Occlusion.NativePointer, OcclusionQuery, 0);

        lock (_gate)
        {
            // A begun pair whose work never ended (the list was dropped) is released.
            if (_open.Remove(key, out var stale))
            {
                stale.Dispose();
            }

            _open[key] = open;
        }
    }

    /// <summary>Ends both queries after the traced work and resolves them into the readback buffer.</summary>
    public void End(nint list, nint key)
    {
        Open? open;

        lock (_gate)
        {
            if (!_open.Remove(key, out open))
            {
                return;
            }
        }

        var end = (delegate* unmanaged<nint, nint, int, uint, void>)ComObject.Slot(list, EndQuerySlot);
        var resolve = (delegate* unmanaged<nint, nint, int, uint, uint, nint, ulong, void>)ComObject.Slot(list, ResolveQueryDataSlot);
        end(list, open.Statistics.NativePointer, StatisticsQuery, 0);
        end(list, open.Occlusion.NativePointer, OcclusionQuery, 0);
        resolve(list, open.Statistics.NativePointer, StatisticsQuery, 0, 1, open.Buffer.NativePointer, 0);
        resolve(list, open.Occlusion.NativePointer, OcclusionQuery, 0, 1, open.Buffer.NativePointer, StatisticsBytes);

        lock (_gate)
        {
            _ended.Add(open);
        }
    }

    /// <summary>Logs the counts of every ended pair; the GPU has finished the lists that recorded them.</summary>
    public void Report()
    {
        lock (_gate)
        {
            foreach (var open in _ended)
            {
                void* data;
                open.Buffer.Map(0, null, &data).CheckError();
                _log.Info($"  [draws] counters of {open.Label}: {Describe(new ReadOnlySpan<ulong>(data, ResultCounters))}");
                open.Buffer.Unmap(0);
                open.Dispose();
            }

            if (_ended.Count > 0 && MeshLaunch.Enabled)
            {
                var launches = MeshLaunch.Counts;
                _log.Info($"  [draws] mesh launches so far: {launches.Launched} ({launches.FromClamp} clamped first), {launches.Dropped} dropped");
            }

            _ended.Clear();
        }
    }

    private sealed record Open(string Label, ID3D12QueryHeap Statistics, ID3D12QueryHeap Occlusion, ID3D12Resource Buffer) : IDisposable
    {
        public void Dispose()
        {
            Statistics.Dispose();
            Occlusion.Dispose();
            Buffer.Dispose();
        }
    }
}
