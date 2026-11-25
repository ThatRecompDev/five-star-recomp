// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Shims.D3D12X.Present;
using FiveStar.Loader.Shims.D3D12X.Resources;
using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// <c>--d3d12-trace-draws</c>: what draws bind, in the log. For the first <see cref="FirstDraws"/> draws and
/// <see cref="WindowDraws"/> draws after each captured present: the pipeline (programs, input layout, target formats,
/// fixed-function state), render targets, viewport, scissor and depth view, the vertex buffers with their first
/// vertices, the graphics user SGPRs and the first records of every descriptor table with the view each names (and a
/// constant buffer's first values). After each such draw the first render target is copied inside the game's list
/// (<see cref="DrawProbes"/>) and reported <see cref="ReportDelay"/> presents later. With <see cref="PipelineFilter"/>,
/// the matching draws of the frame after a captured present are logged too (no probes). Debug only: it allocates and locks.
/// </summary>
internal sealed unsafe class DrawTrace
{
    private const int FirstDraws = 24;

    /// <summary>Render targets a traced ExecuteIndirect reads back (the lit HDR and the tiled lighting's second target).</summary>
    private const int TargetsReadBack = 2;
    private const int WindowDraws = 32;

    /// <summary>Targets read back in one dumped frame at most: each costs a readback buffer of its size.</summary>
    private const int DumpsPerFrame = 96;

    /// <summary>Narrower targets are not dumped: the mip chains and probes filled the cap before the G-buffer.</summary>
    private const int DumpMinimumWidth = 1024;
    private const long ReportDelay = 2;
    private const uint RecordsShown = 8;
    private const uint VerticesShown = 3;
    private const int UserDwordsShown = 32;
    private const int ConstantsShown = 16;

    /// <summary>Draws matching <see cref="PipelineFilter"/> logged per traced frame.</summary>
    private const int FilterDraws = 64;

    /// <summary>The largest high dword of a console GPU address (48-bit addresses).</summary>
    private const uint MaxAddressHigh = 0xFFFF;

    /// <summary>Argument records logged for a traced ExecuteIndirect.</summary>
    private const uint IndirectRecordsShown = 4;

    /// <summary>Floats of a D3D12_VIEWPORT and ints of a D3D12_RECT (d3d12.h).</summary>
    private const int ViewportFloats = 6;
    private const int RectangleInts = 4;
    private const uint TitleDwordsShown = 4;
    private const uint FloatBytes = sizeof(float);
    private const int DwordBits = 32;

    /// <summary>D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV and ID3D12Device::GetDescriptorHandleIncrementSize (d3d12.h).</summary>
    private const int ViewHeapType = 0;

    /// <summary>D3D12_RESOURCE_DIMENSION_TEXTURE3D.</summary>
    private const int Texture3D = 4;
    private const int IncrementSlot = 15;

    private readonly TracedViews _views = new();
    private readonly ConcurrentDictionary<nint, ulong> _constantBuffers = new();
    private readonly ConcurrentDictionary<nint, (nint Resource, int Home)> _targetViews = new();
    private readonly ConcurrentDictionary<nint, (int Game, int View)> _viewFormats = new();
    private readonly ConcurrentDictionary<nint, string> _targetDescriptions = new();
    private readonly ConcurrentDictionary<nint, int> _dumped = new();
    private readonly ConcurrentDictionary<nint, byte> _indirectProbes = new();
    private readonly ConcurrentDictionary<nint, long> _targetWidths = new();
    private long _dumpPresent;
    private int _dumps;
    private readonly ConcurrentDictionary<nint, DrawTraceList> _lists = new();
    private readonly FrameCapture _capture;
    private readonly DrawProbes _probes;
    private readonly DrawCensus _census;
    private readonly GameMemorySource _memory;
    private readonly XboxDeviceFactory _d3d12;
    private readonly LoaderLog _log;
    private long _draws;
    private long _windowEnd = FirstDraws;
    private long _reportAt;
    private long _lastPresent;
    private bool _filterOpen;
    private int _filtered;

    public DrawTrace(FrameCapture capture, GameMemorySource memory, XboxDeviceFactory d3d12, LoaderLog log)
    {
        _capture = capture;
        _probes = new DrawProbes(log);
        _census = new DrawCensus(log);
        _memory = memory;
        _d3d12 = d3d12;
        _log = log;
    }

    /// <summary>Also logs, in the frame after each captured present, the draws whose pipeline name holds this text.</summary>
    public string? PipelineFilter { get; init; }

    /// <summary>A view was written at desktop CPU handle <paramref name="handle"/> (no texture a probe can read there).</summary>
    public void View(nint handle, string text)
    {
        _views.Write(handle, text);
    }

    /// <summary>Slot 70 wrote a view of <paramref name="texture"/> (null: a buffer or null view) at <paramref name="handle"/>.</summary>
    public void AddressView(nint handle, ResourceDescription* resource, AddressViewDescription* view, GpuTexture? texture)
    {
        string console = resource == null ? "no desc" : $"{resource->Width}x{resource->Height} dim {resource->Dimension} fmt {resource->Format}";
        string desktop = "null view";

        if (texture is null && _d3d12.GameMemory.TryFind(view->Address, out var range) && range.Resource != 0)
        {
            desktop = $"buffer view on 0x{range.Resource:x} +0x{view->Address - range.GameAddress:x}";
            _constantBuffers[handle] = view->Address;
        }

        if (texture is not null)
        {
            string writer = texture.GpuWritten ? "GPU-written" : $"from memory, hash 0x{texture.UploadedHash:x}";
            desktop = $"texture 0x{texture.Desktop:x} at 0x{texture.Address:x} fmt {texture.DesktopFormat}, {writer}";
        }

        string text = $"slot70 0x{view->Address:x} ({console}, view fmt {view->Format}) -> {desktop}";

        if (texture is null)
        {
            View(handle, text);

            return;
        }

        _views.Write(handle, text, texture.Desktop, texture.State);
    }

    /// <summary>Slot 17 wrote a CBV into the game's heaps (a raw SRV) at <paramref name="handle"/>.</summary>
    public void ConstantBufferView(nint handle, ulong address, uint size)
    {
        _constantBuffers[handle] = address;
        View(handle, $"CBV 0x{address:x} (0x{size:x} bytes) as raw SRV");
    }

    /// <summary>
    /// Slot 18 wrote a view of desktop <paramref name="resource"/> at <paramref name="handle"/>; with the desc the desktop
    /// got (<paramref name="description"/>), a texture view also names its format and, for a 2D view, its plane.
    /// </summary>
    public void ShaderResourceView(nint resource, nint handle, nint wrapper = 0, void* description = null)
    {
        var game = _d3d12.Objects.AsResource(wrapper);
        string text = resource == 0 ? "SRV of null" : $"SRV of 0x{resource:x} {Describe(resource)}{ViewFormat(description)}";

        if (resource != 0 && game != null)
        {
            _views.Write(handle, text, resource, game->HomeState, ViewPlane(description));

            return;
        }

        View(handle, text);
    }

    /// <summary>The plane slice of a 2D or 2D-array SRV desc (1: a depth-stencil's stencil); 0 for any other view.</summary>
    internal static int ViewPlane(void* description)
    {
        const int Texture2D = 4;
        const int Texture2DArray = 5;
        const int Texture2DPlaneDword = 6;
        const int Texture2DArrayPlaneDword = 8;

        if (description == null)
        {
            return 0;
        }

        int* view = (int*)description;

        return view[1] switch
        {
            Texture2D => view[Texture2DPlaneDword],
            Texture2DArray => view[Texture2DArrayPlaneDword],
            _ => 0,
        };
    }

    /// <summary>", view format F" (and ", plane P" for a 2D or 2D-array view) of an SRV desc; empty for a buffer view or none.</summary>
    private static string ViewFormat(void* description)
    {
        const int Texture2D = 4;
        const int Texture2DArray = 5;
        const int Texture2DPlaneDword = 6;
        const int Texture2DArrayPlaneDword = 8;

        if (description == null || ((int*)description)[1] == BufferViewDescription.BufferDimension)
        {
            return string.Empty;
        }

        int* view = (int*)description;

        if (view[1] == Texture2D)
        {
            return $", view format {view[0]}, plane {view[Texture2DPlaneDword]}";
        }

        if (view[1] == Texture2DArray)
        {
            return $", view format {view[0]}, plane {view[Texture2DArrayPlaneDword]}";
        }

        return $", view format {view[0]}";
    }

    /// <summary>Slot 19 wrote a UAV of desktop <paramref name="resource"/> at <paramref name="handle"/> (a writer's target).</summary>
    public void UnorderedAccessView(nint resource, nint handle)
    {
        string text = resource == 0 ? "UAV of null" : $"UAV of 0x{resource:x} {Describe(resource)}";
        View(handle, text);
    }

    /// <summary>A render-target view of <paramref name="resource"/> (a wrapper or a desktop resource).</summary>
    public void RenderTargetView(nint resource, nint handle, int gameFormat = 0, int viewFormat = 0)
    {
        _viewFormats[handle] = (gameFormat, viewFormat);
        var wrapper = _d3d12.Objects.AsResource(resource);
        int home = wrapper == null ? States.ResourceStates.RenderTarget : wrapper->HomeState;
        nint desktop = _d3d12.Objects.Unwrap(resource);
        _targetViews[handle] = (desktop, home);

        // Described while alive: the census reports targets the game may have released since.
        _targetDescriptions[desktop] = Describe(desktop);
        _targetWidths[desktop] = Width(desktop);
    }

    /// <summary>
    /// With <c>--d3d12-dump-targets</c>: in the frame after each captured present, every render and depth target is
    /// read back the 1st, 2nd, 4th, 8th... time a list leaves it (its passes one after another) and written as a PNG beside the captures.
    /// </summary>
    public static bool DumpTargets { get; set; }

    /// <summary>
    /// <c>--d3d12-probe-traced</c>: each draw <see cref="PipelineFilter"/> traces also reads back its first render target
    /// (a float target's mean and largest channels too): the exposure texel, the lit HDR, the composite.
    /// </summary>
    public static bool ProbeTraced { get; set; }

    /// <summary>List slot 50 (<paramref name="single"/> ranges are not followed).</summary>
    public void RenderTargets(XboxObject* list, uint count, nint* handles, bool single, nint depthStencil)
    {
        uint used = handles == null || single ? 0 : count;
        var targets = new nint[used];

        for (int i = 0; i < targets.Length; i++)
        {
            targets[i] = handles[i];
        }

        var state = List(list);

        if (Volatile.Read(ref _dumpPresent) != 0)
        {
            Dump(list, state);
        }

        state.Targets = targets;
        state.DepthStencil = depthStencil;
    }

    /// <summary>List slot 21: the first viewport.</summary>
    public void Viewports(XboxObject* list, uint count, float* viewports)
    {
        List(list).Viewport = Values(count, viewports, ViewportFloats);
    }

    /// <summary>List slot 22: the first scissor rectangle.</summary>
    public void Scissors(XboxObject* list, uint count, int* rects)
    {
        List(list).Scissor = Rectangle(count, rects);
    }

    /// <summary>List slot 48 with the console addresses.</summary>
    public void VertexBuffers(XboxObject* list, uint start, uint count, VertexBufferView* views)
    {
        var text = new StringBuilder();

        for (uint i = 0; i < count; i++)
        {
            var view = views[i];
            bool translated = _d3d12.GameMemory.TryTranslate(view.Location, out ulong desktop);
            string bound = translated ? $"desktop 0x{desktop:x}" : "unbound";
            text.Append($"\n      vb{start + i}: 0x{view.Location:x} size 0x{view.Size:x} stride {view.StrideOrFormat} -> {bound}");
            AppendVertices(text, view);
        }

        List(list).VertexBuffers = text.ToString();
    }

    /// <summary>A draw on <paramref name="list"/>; logged inside a trace window.</summary>
    public void Draw(XboxObject* list, uint vertices, uint instances)
    {
        long draw = Interlocked.Increment(ref _draws);
        var rectangles = RectangleListState.Of(list);
        bool filtered = draw > Volatile.Read(ref _windowEnd);

        if (filtered && !Matches(rectangles->Pipeline))
        {
            return;
        }

        var state = List(list);
        state.Traced = true;
        string pipeline = Label(rectangles->Pipeline);
        var text = new StringBuilder($"  [draws] draw {draw}: list 0x{(nint)list:x}, {vertices} vertices x {instances}");
        text.Append($"\n      pipeline 0x{rectangles->Pipeline:x} topology {rectangles->Topology} variant {rectangles->VariantBound}: {pipeline}");

        AppendTargets(text, state);
        text.Append($"\n      viewport {state.Viewport}; scissor {state.Scissor}; dsv 0x{state.DepthStencil:x}");
        text.Append(state.VertexBuffers);
        AppendUserData(text, CommandListBindings.Of(list), compute: false);

        if (filtered)
        {
            AppendWindowRecords(text, CommandListBindings.Of(list), compute: false);
            state.ProbeNext = ProbeTraced;
        }

        _log.Info(text.ToString());
    }

    /// <summary>The window records at <see cref="TraceRecords.Offsets"/> from the user SGPRs (<c>--d3d12-trace-records</c>).</summary>
    private void AppendWindowRecords(StringBuilder text, CommandListBindings* bindings, bool compute)
    {
        var sources = new TraceRecordSources(MapState, (address, bytes) => (nint)_memory.Find(address, (ulong)bytes), GameRecord, TextureState, ViewAt);
        TraceRecords.Append(text, _d3d12.Bindings.Window.Data, DescriptorWindow.NullTableBase, Sgprs(bindings, compute), TraceRecords.Offsets, sources);
    }

    /// <summary>The user SGPRs the list's next draw (graphics) or dispatch (compute) gets.</summary>
    private static ReadOnlySpan<uint> Sgprs(CommandListBindings* bindings, bool compute)
    {
        if (compute)
        {
            return new ReadOnlySpan<uint>(bindings->Compute, UserDwordsShown);
        }

        return new ReadOnlySpan<uint>(bindings->Graphics, UserDwordsShown);
    }

    /// <summary>
    /// A pipeline's name in the trace: its rectangle-list description (graphics), else the programs it was built from
    /// (compute and mesh pipelines; <see cref="QueueProgress.PipelineName"/>).
    /// </summary>
    private string Label(nint pipeline)
    {
        string? described = _d3d12.Rectangles.Pipelines?.Describe(pipeline);

        if (described is not null)
        {
            return described;
        }

        return QueueProgress.PipelineName(pipeline);
    }

    /// <summary>The view this trace saw written at desktop heap index <paramref name="index"/> (resource, size and format).</summary>
    private string ViewAt(uint index)
    {
        var window = _d3d12.Bindings.Window;

        if (window.ViewHeap == 0)
        {
            return "no view heap";
        }

        nint device = _d3d12.Objects.DesktopDevice;
        uint increment = ((delegate* unmanaged<nint, int, uint>)ComObject.Slot(device, IncrementSlot))(device, ViewHeapType);
        nint handle = DesktopCalls.HeapCpuStart(window.ViewHeap) + (nint)((ulong)index * increment);

        if (_views.TryText(handle, out var known))
        {
            return known;
        }

        return "not a view this trace saw";
    }

    /// <summary>How the texture registered at <paramref name="address"/> got its texels: GPU-written, copied into, or uploaded from memory.</summary>
    private string TextureState(ulong address)
    {
        var texture = _d3d12.Textures.Registry.Find(address);

        if (texture is null)
        {
            return "no texture registered there";
        }

        string source = $"from memory, hash 0x{texture.UploadedHash:x}";

        if (texture.GpuWritten)
        {
            source = "GPU-written";
        }

        if (texture.CopiedInto)
        {
            source += ", copied into";
        }

        return $"texture 0x{texture.Desktop:x} at 0x{texture.Address:x} fmt {texture.DesktopFormat}, {source}";
    }

    /// <summary>The game's own view-heap record behind window offset <paramref name="windowOffset"/>; 0 past its heap.</summary>
    private nint GameRecord(uint windowOffset)
    {
        var heap = _d3d12.DescriptorHeaps.Views;
        uint index = (windowOffset - DescriptorWindow.ViewBase) / DescriptorWindow.ViewRecordBytes;

        if (heap is null || index >= heap.Count)
        {
            return 0;
        }

        return heap.Title + (nint)((ulong)index * heap.Increment);
    }

    /// <summary>
    /// An ExecuteIndirect whose pipeline matches <see cref="PipelineFilter"/> in the frame after a captured present: its
    /// first argument records, the count's source, targets and the bindings, as a traced draw; its targets are read back
    /// right after it (<see cref="AfterIndirect"/>), so one mesh pass shows whether it wrote anything.
    /// </summary>
    public void Indirect(XboxObject* list, uint maxCount, ulong argumentAddress, uint stride, bool counted, bool compute = false)
    {
        var rectangles = RectangleListState.Of(list);

        if (!Matches(rectangles->Pipeline))
        {
            return;
        }

        var state = List(list);
        state.Traced = true;
        string kind = compute ? "compute indirect" : "indirect";
        var text = new StringBuilder($"  [draws] {kind}: list 0x{(nint)list:x}, max {maxCount} records of {stride} bytes, count buffer {counted}");
        text.Append($"\n      pipeline 0x{rectangles->Pipeline:x} topology {rectangles->Topology}: {Label(rectangles->Pipeline)}");
        AppendRecords(text, argumentAddress, Math.Min(maxCount, IndirectRecordsShown), stride);

        if (!compute)
        {
            AppendTargets(text, state);
            text.Append($"\n      viewport {state.Viewport}; scissor {state.Scissor}; dsv 0x{state.DepthStencil:x}");
        }

        AppendUserData(text, CommandListBindings.Of(list), compute);
        AppendPointers(text, CommandListBindings.Of(list), compute);
        AppendWindowRecords(text, CommandListBindings.Of(list), compute);

        _log.Info(text.ToString());
        _indirectProbes[(nint)list] = 0;

        if (ProbeTraced)
        {
            ProbeRecords(list, state, compute);
        }
    }

    /// <summary>
    /// A Dispatch whose compute pipeline matches <see cref="PipelineFilter"/> in the frame after a captured present: its
    /// group counts, the compute user SGPRs and their tables, and the <c>--d3d12-trace-records</c> records, as a traced
    /// ExecuteIndirect. With <see cref="ProbeTraced"/> the texture SRVs those records name are read back right after it
    /// (<see cref="AfterDispatch"/>), so a pass shows what its own UAVs or its inputs hold.
    /// </summary>
    public void Dispatch(XboxObject* list, uint x, uint y, uint z)
    {
        nint pipeline = RectangleListState.Of(list)->Pipeline;

        if (!Matches(pipeline))
        {
            return;
        }

        var state = List(list);
        state.Traced = true;
        var text = new StringBuilder($"  [draws] dispatch: list 0x{(nint)list:x}, groups {x} x {y} x {z}");
        text.Append($"\n      pipeline 0x{pipeline:x}: {Label(pipeline)}");

        AppendUserData(text, CommandListBindings.Of(list), compute: true);
        AppendPointers(text, CommandListBindings.Of(list), compute: true);
        AppendWindowRecords(text, CommandListBindings.Of(list), compute: true);

        _log.Info(text.ToString());
        state.ProbeDispatch = ProbeTraced;
    }

    /// <summary>After a traced Dispatch: the texture SRVs its traced records name are read back in the list.</summary>
    public void AfterDispatch(XboxObject* list)
    {
        if (!_lists.TryGetValue((nint)list, out var state) || !state.ProbeDispatch)
        {
            return;
        }

        state.ProbeDispatch = false;
        ProbeRecords(list, state, compute: true);
        Interlocked.CompareExchange(ref _reportAt, Volatile.Read(ref _lastPresent) + ReportDelay, 0);
    }

    /// <summary>
    /// The game submitted <paramref name="count"/> lists on queue number <paramref name="queue"/>: each that recorded traced
    /// work is named with the queue, so a traced pass shows where (and whether) it ran.
    /// </summary>
    public void Submitted(int queue, nint* lists, uint count)
    {
        for (uint i = 0; i < count; i++)
        {
            if (_lists.TryGetValue(lists[i], out var state) && state.Traced)
            {
                state.Traced = false;
                _log.Info($"  [draws] list 0x{lists[i]:x} with traced work submitted on queue #{queue}");
            }
        }
    }

    /// <summary>
    /// With <see cref="ProbeTraced"/>, before a traced ExecuteIndirect runs: each texture SRV the
    /// <c>--d3d12-trace-records</c> offsets name by heap index, in every table the trace prints them from (the same
    /// SGPRs, <see cref="TraceRecords.Tables"/>), is read back as the draw will see it, labelled with SGPR and heap index.
    /// </summary>
    private void ProbeRecords(XboxObject* list, DrawTraceList state, bool compute)
    {
        var window = _d3d12.Bindings.Window;

        if (window.Data == null || window.ViewHeap == 0)
        {
            return;
        }

        nint device = _d3d12.Objects.DesktopDevice;
        uint increment = ((delegate* unmanaged<nint, int, uint>)ComObject.Slot(device, IncrementSlot))(device, ViewHeapType);
        nint start = DesktopCalls.HeapCpuStart(window.ViewHeap);

        foreach (var (sgpr, table) in TraceRecords.Tables(Sgprs(CommandListBindings.Of(list), compute), DescriptorWindow.NullTableBase, TraceRecords.Offsets))
        {
            foreach (var entry in TraceRecords.Offsets)
            {
                uint? index = TraceRecords.HeapIndex((uint*)(window.Data + table + entry.Offset));

                if (!index.HasValue || !_views.TryTexture(start + (nint)((ulong)index.Value * increment), out var view))
                {
                    continue;
                }

                int current = _d3d12.ListStates.Find(list)?.Current(view.Resource, view.Home) ?? view.Home;
                string label = $"record sgpr {sgpr} +0x{entry.Offset:x}, heap index {index.Value} (texture 0x{view.Resource:x} {Describe(view.Resource)}, plane {view.Plane})";
                _probes.Record(device, list->Inner, view.Resource, current, label, (nint)list, state.Resets, plane: view.Plane);
            }
        }
    }

    /// <summary>After a traced ExecuteIndirect: its first two render targets and its depth target are read back in the list.</summary>
    public void AfterIndirect(XboxObject* list)
    {
        if (!_indirectProbes.TryRemove((nint)list, out _))
        {
            return;
        }

        var state = List(list);

        foreach (nint handle in state.Targets.Take(TargetsReadBack).Append(state.DepthStencil))
        {
            if (handle == 0 || !_targetViews.TryGetValue(handle, out var target))
            {
                continue;
            }

            int current = _d3d12.ListStates.Find(list)?.Current(target.Resource, target.Home) ?? target.Home;
            string label = $"indirect on list 0x{(nint)list:x} (target 0x{target.Resource:x} {_targetDescriptions.GetValueOrDefault(target.Resource, "?")})";
            _probes.Record(_d3d12.Objects.DesktopDevice, list->Inner, target.Resource, current, label, (nint)list, state.Resets);
            Interlocked.CompareExchange(ref _reportAt, Volatile.Read(ref _lastPresent) + ReportDelay, 0);
        }
    }

    private void AppendRecords(StringBuilder text, ulong address, uint records, uint stride)
    {
        uint words = stride / sizeof(uint);
        var data = address == 0 || words == 0 ? null : (uint*)_memory.Find(address, (ulong)records * stride);

        if (data == null)
        {
            text.Append($"\n      arguments at 0x{address:x}: not readable");

            return;
        }

        for (uint record = 0; record < records; record++)
        {
            text.Append($"\n      record {record}:");

            for (uint word = 0; word < words; word++)
            {
                text.Append($" {data[(record * words) + word]}");
            }
        }
    }

    /// <summary>After a traced draw: the list's first render target is copied for <see cref="DrawProbes"/>.</summary>
    public void AfterDraw(XboxObject* list)
    {
        long draw = Interlocked.Read(ref _draws);
        var state = List(list);

        bool traced = state.ProbeNext;
        state.ProbeNext = false;

        if ((draw > Volatile.Read(ref _windowEnd) && !traced) || state.Targets.Length == 0 || !_targetViews.TryGetValue(state.Targets[0], out var target))
        {
            return;
        }

        int current = _d3d12.ListStates.Find(list)?.Current(target.Resource, target.Home) ?? target.Home;
        _probes.Record(_d3d12.Objects.DesktopDevice, list->Inner, target.Resource, current, $"draw {draw} (target 0x{target.Resource:x})", (nint)list, state.Resets);
        Interlocked.CompareExchange(ref _reportAt, Volatile.Read(ref _lastPresent) + ReportDelay, 0);
    }

    /// <summary>The game reset <paramref name="list"/>: probes copied on it can no longer run again.</summary>
    public void Reset(XboxObject* list)
    {
        if (_lists.TryGetValue((nint)list, out var state))
        {
            state.Resets++;
        }
    }

    /// <summary>A draw, dispatch or ExecuteIndirect of <paramref name="pipeline"/> (a stand-in when skipped), for the census.</summary>
    [HotPath]
    public void Census(XboxObject* list, nint pipeline, bool skipped)
    {
        nint target = 0;

        if (_lists.TryGetValue((nint)list, out var state) && state.Targets.Length > 0 && _targetViews.TryGetValue(state.Targets[0], out var view))
        {
            target = view.Resource;
        }

        _census.Add(pipeline, target, skipped);
    }

    /// <summary>Called after every present; true when probes are due (the caller then waits for the GPU and calls <see cref="Report"/>).</summary>
    public bool ReportDue(long present)
    {
        bool wanted = _capture.Wants(present);
        Volatile.Write(ref _filterOpen, wanted);
        Interlocked.Exchange(ref _filtered, 0);

        if (wanted)
        {
            Volatile.Write(ref _windowEnd, Interlocked.Read(ref _draws) + WindowDraws);
            _census.Report(present, PipelineName, resource => _targetDescriptions.GetValueOrDefault(resource, "?"), standIn => _d3d12.Pipelines?.StandIns.State(standIn) ?? "unknown");
        }

        // Targets are dumped during the one frame after a captured present.
        _dumped.Clear();
        Volatile.Write(ref _dumps, 0);
        long dumping = 0;

        if (wanted && DumpTargets)
        {
            dumping = present;
        }

        Volatile.Write(ref _dumpPresent, dumping);
        Volatile.Write(ref _lastPresent, present);

        return present == Volatile.Read(ref _reportAt);
    }

    /// <summary>Logs the probes; the GPU finished everything submitted.</summary>
    public void Report()
    {
        _probes.Report(Resets);
        Volatile.Write(ref _reportAt, 0);
    }

    /// <summary>
    /// In the frame after a captured present, true for at most <see cref="FilterDraws"/> draws whose pipeline name holds
    /// <see cref="PipelineFilter"/> (<c>--d3d12-trace-pipeline</c>): the frame's UI draws come long after its first.
    /// </summary>
    private bool Matches(nint pipeline)
    {
        if (PipelineFilter is null || !Volatile.Read(ref _filterOpen) || Volatile.Read(ref _filtered) >= FilterDraws)
        {
            return false;
        }

        // The programs the pipeline was built from (mesh pipelines have no rectangle-list description), then that description.
        string name = QueueProgress.PipelineName(pipeline) + " " + (_d3d12.Rectangles.Pipelines?.Describe(pipeline) ?? string.Empty);

        return MatchesFilter(name, PipelineFilter) && Interlocked.Increment(ref _filtered) <= FilterDraws;
    }

    /// <summary>Whether <paramref name="name"/> holds one of the comma-separated parts of <paramref name="filter"/>.</summary>
    public static bool MatchesFilter(string name, string filter)
    {
        foreach (var part in filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (name.Contains(part, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private long Resets(nint list)
    {
        if (_lists.TryGetValue(list, out var state))
        {
            return state.Resets;
        }

        return 0;
    }

    /// <summary>The list leaves its targets: each one not read back yet this frame is copied now, in the list.</summary>
    private void Dump(XboxObject* list, DrawTraceList state)
    {
        long present = Volatile.Read(ref _dumpPresent);

        foreach (nint handle in state.Targets.Append(state.DepthStencil))
        {
            if (handle == 0 || !_targetViews.TryGetValue(handle, out var target) || _targetWidths.GetValueOrDefault(target.Resource) < DumpMinimumWidth)
            {
                continue;
            }

            // The 1st, 2nd, 4th, 8th... time a list leaves the target this frame: its passes one after another.
            int leave = _dumped.AddOrUpdate(target.Resource, 1, static (_, count) => count + 1);

            if ((leave & (leave - 1)) != 0)
            {
                continue;
            }

            if (Interlocked.Increment(ref _dumps) > DumpsPerFrame)
            {
                return;
            }

            int current = _d3d12.ListStates.Find(list)?.Current(target.Resource, target.Home) ?? target.Home;
            string? png = null;

            if (_capture.Directory is not null)
            {
                png = Path.Combine(_capture.Directory, $"present-{present}-target-0x{target.Resource:x}-leave-{leave}.png");
            }

            string label = $"present {present} target 0x{target.Resource:x} {_targetDescriptions.GetValueOrDefault(target.Resource, "?")} leave {leave}";
            _probes.Record(_d3d12.Objects.DesktopDevice, list->Inner, target.Resource, current, label, (nint)list, state.Resets, png);
            Interlocked.CompareExchange(ref _reportAt, Volatile.Read(ref _lastPresent) + ReportDelay, 0);
        }
    }

    private string PipelineName(nint pipeline, bool standIn)
    {
        if (standIn)
        {
            return _d3d12.Pipelines?.StandIns.Name(pipeline) ?? "unnamed";
        }

        return QueueProgress.PipelineName(pipeline);
    }

    private DrawTraceList List(XboxObject* list)
    {
        return _lists.GetOrAdd((nint)list, static _ => new DrawTraceList());
    }

    private void AppendTargets(StringBuilder text, DrawTraceList state)
    {
        foreach (nint handle in state.Targets)
        {
            string target = _targetViews.TryGetValue(handle, out var view) ? $"resource 0x{view.Resource:x} {Describe(view.Resource)}, home state 0x{view.Home:x}{ViewFormat(handle)}" : "not a view this trace saw";
            text.Append($"\n      rtv 0x{handle:x}: {target}");
        }
    }

    /// <summary>The view's format as the game asked for it and as the desktop got it (0: the resource's own).</summary>
    private string ViewFormat(nint handle)
    {
        if (!_viewFormats.TryGetValue(handle, out var formats))
        {
            return string.Empty;
        }

        return $", view format {formats.View} (game {formats.Game})";
    }

    private void AppendVertices(StringBuilder text, VertexBufferView view)
    {
        uint stride = view.StrideOrFormat;
        ulong bytes = Math.Min(view.Size, stride * VerticesShown);
        byte* data = bytes == 0 ? null : _memory.Find(view.Location, bytes);

        if (data == null)
        {
            text.Append("\n        (memory not readable)");

            return;
        }

        for (uint vertex = 0; vertex < VerticesShown && (vertex + 1) * stride <= bytes; vertex++)
        {
            text.Append($"\n        v{vertex}:");

            for (uint offset = 0; offset + FloatBytes <= stride; offset += FloatBytes)
            {
                byte* value = data + (vertex * stride) + offset;
                text.Append($" {*(float*)value:G4}/{*(uint*)value:x}");
            }
        }
    }

    private void AppendUserData(StringBuilder text, CommandListBindings* bindings, bool compute)
    {
        var sgprs = Sgprs(bindings, compute);
        string header = compute ? "\n      compute user sgprs:" : "\n      user sgprs:";
        text.Append(header);

        foreach (uint value in sgprs)
        {
            text.Append($" {value:x}");
        }

        var layout = compute ? bindings->ComputeLayout : bindings->GraphicsLayout;
        uint first = compute ? UserData.ComputeBase : UserData.GraphicsBase;

        for (uint parameter = 0; layout != null && parameter < (uint)layout->Count; parameter++)
        {
            int sgpr = (int)(first + layout->Offsets[parameter]);
            var kind = layout->Kind(parameter);

            if (sgpr + 1 >= sgprs.Length)
            {
                continue;
            }

            if (kind == RootParameterKind.Table)
            {
                AppendTable(text, parameter, sgprs[sgpr], layout->Extents[parameter]);
            }
            else if (kind != RootParameterKind.Constants)
            {
                AppendRootAddress(text, parameter, sgprs[sgpr] | ((ulong)sgprs[sgpr + 1] << DwordBits));
            }
        }
    }

    /// <summary>
    /// User SGPR pairs that hold a console GPU address (high dword 1 to 0xFFFF): whether the address map has a desktop
    /// page there (a program reading through an unmapped page gets zeros) and the first dwords.
    /// </summary>
    private void AppendPointers(StringBuilder text, CommandListBindings* bindings, bool compute)
    {
        var sgprs = Sgprs(bindings, compute);

        for (int i = 0; i + 1 < sgprs.Length; i++)
        {
            uint high = sgprs[i + 1];

            if (high == 0 || high > MaxAddressHigh)
            {
                continue;
            }

            ulong address = sgprs[i] | ((ulong)high << DwordBits);
            text.Append($"\n      sgprs {i}-{i + 1} 0x{address:x}: {MapState(address)}");
        }
    }

    /// <summary>
    /// What translated programs read at <paramref name="address"/>: the entry of the address map their <c>gpu_page</c>
    /// reads (view index and offset field). The null view means the GPU reads zeros there.
    /// </summary>
    private string MapState(ulong address)
    {
        var (view, field) = _d3d12.MemoryViews.Entry(address);
        string range = _d3d12.GameMemory.TryTranslate(address, out ulong desktop) ? $"buffer 0x{desktop:x}" : "no buffer";

        if (view == Binding.DescriptorWindow.UnmappedSlot || view == 0)
        {
            return $"UNMAPPED in the address map (view {view}, field 0x{field:x}): programs read zeros there; {range}";
        }

        return $"mapped (view {view}, field 0x{field:x}); {range}";
    }

    /// <summary>A root CBV, SRV or UAV: its console address and the first dwords there.</summary>
    private void AppendRootAddress(StringBuilder text, uint parameter, ulong address)
    {
        text.Append($"\n      root descriptor {parameter} at 0x{address:x}, {MapState(address)}:");
        var data = (uint*)_memory.Find(address, ConstantsShown * sizeof(uint));

        if (data == null)
        {
            text.Append(" (memory not readable)");

            return;
        }

        for (int i = 0; i < ConstantsShown; i++)
        {
            text.Append($" {data[i]:x}");
        }
    }

    private void AppendTable(StringBuilder text, uint parameter, uint pointer, uint extent)
    {
        var window = _d3d12.Bindings.Window;
        text.Append($"\n      table {parameter} at window 0x{pointer:x} (root signature: {extent} records)");

        if (window.Data == null || window.ViewHeap == 0)
        {
            return;
        }

        nint start = DesktopCalls.HeapCpuStart(window.ViewHeap);
        nint device = _d3d12.Objects.DesktopDevice;
        uint increment = ((delegate* unmanaged<nint, int, uint>)ComObject.Slot(device, IncrementSlot))(device, ViewHeapType);

        for (uint record = 0; record < RecordsShown; record++)
        {
            uint offset = pointer + (record * DescriptorWindow.ViewRecordBytes);
            uint index = ((uint*)(window.Data + offset))[Binding.DescriptorWindow.MirroredDesktopDword];
            nint handle = start + (nint)((ulong)index * increment);
            string view = _views.TryText(handle, out var known) ? known : "not a view this trace saw";

            text.Append($"\n        [{record}] desktop {index}: {view}");
            AppendTitleRecord(text, offset);
            AppendConstants(text, handle);
        }
    }

    private void AppendConstants(StringBuilder text, nint handle)
    {
        byte* data = _constantBuffers.TryGetValue(handle, out ulong address) ? _memory.Find(address, ConstantsShown * sizeof(float)) : null;

        if (data == null)
        {
            return;
        }

        text.Append("\n            values:");

        for (int i = 0; i < ConstantsShown; i++)
        {
            text.Append($" {((float*)data)[i]:G4}");
        }
    }

    /// <summary>The game's own record behind a window record, at draw time.</summary>
    private void AppendTitleRecord(StringBuilder text, uint windowOffset)
    {
        var heap = _d3d12.DescriptorHeaps.Views;

        if (heap is null || windowOffset >= DescriptorWindow.NullTableBase)
        {
            return;
        }

        uint index = (windowOffset - DescriptorWindow.ViewBase) / DescriptorWindow.ViewRecordBytes;
        var record = (uint*)(heap.Title + (nint)((ulong)index * heap.Increment));
        text.Append($"\n            title record {index}:");

        for (uint dword = 0; dword < TitleDwordsShown; dword++)
        {
            text.Append($" {record[dword]:x8}");
        }
    }

    /// <summary>The first <paramref name="length"/> floats (a D3D12_VIEWPORT: x, y, width, height, min and max depth).</summary>
    private static string Values(uint count, float* values, int length)
    {
        if (count == 0 || values == null)
        {
            return string.Empty;
        }

        return string.Join(' ', Enumerable.Range(0, length).Select(i => values[i].ToString("G6", System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>The first D3D12_RECT (left, top, right, bottom).</summary>
    private static string Rectangle(uint count, int* rect)
    {
        if (count == 0 || rect == null)
        {
            return string.Empty;
        }

        return string.Join(' ', Enumerable.Range(0, RectangleInts).Select(i => rect[i]));
    }

    private static long Width(nint resource)
    {
        if (resource == 0)
        {
            return 0;
        }

        Marshal.AddRef(resource);
        using var desktop = new ID3D12Resource(resource);

        return (long)desktop.Description.Width;
    }

    private static string Describe(nint resource)
    {
        if (resource == 0)
        {
            return "null";
        }

        Marshal.AddRef(resource);
        using var desktop = new ID3D12Resource(resource);
        var description = desktop.Description;

        return DescribeShape((int)description.Dimension, description.Width, description.Height, description.DepthOrArraySize, description.MipLevels, (int)description.Format);
    }

    /// <summary>"WxH format F", with ", 3D depth D", ", N slices" and ", M mips" when they are not 1 (a buffer is only its width).</summary>
    internal static string DescribeShape(int dimension, ulong width, uint height, int depth, int mips, int format)
    {
        string shape = $"{width}x{height} format {format}";

        if (dimension == Texture3D)
        {
            shape += $", 3D depth {depth}";
        }
        else if (depth > 1)
        {
            shape += $", {depth} slices";
        }

        if (mips > 1)
        {
            shape += $", {mips} mips";
        }

        return shape;
    }
}
