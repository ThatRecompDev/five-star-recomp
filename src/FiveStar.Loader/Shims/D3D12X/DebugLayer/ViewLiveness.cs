// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Binding;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// <c>--check-view-liveness</c>: before each draw, dispatch and ExecuteIndirect, every record of the descriptor tables
/// the root signature declares is checked against what the shim wrote behind it (w78's removal, DRED page fault at VA
/// 0, during CS_UpdateParticles): the desktop resource of its view must still be alive (not released by its last
/// Release), a UAV range must hold a UAV and an SRV range none, and a buffer record (one with num_records) must name a
/// buffer view, a record without V# fields a texture. The views are noted where the shim writes or copies them, by
/// desktop CPU handle, so a check is a lookup, not a D3D call. Each new (pipeline, record, reason) is logged once and
/// the reasons are counted for the sample line.
/// </summary>
internal static unsafe class ViewLiveness
{
    /// <summary>SRV and UAV desc field: D3D12_SRV_DIMENSION_BUFFER and D3D12_UAV_DIMENSION_BUFFER are both 1, at +4.</summary>
    private const int DimensionOffset = 4;
    private const int BufferDimension = 1;

    /// <summary>ID3D12Resource::GetDesc (d3d12.h).</summary>
    private const int GetDescSlot = 10;

    /// <summary>The record dword with num_records: nonzero for a buffer view only.</summary>
    private const int RecordsDword = 2;

    private const int LoggedFindings = 256;

    private static readonly ConcurrentDictionary<nint, SlotView> Slots = new();
    private static readonly ConcurrentDictionary<nint, byte> Live = new();
    private static readonly ConcurrentDictionary<(nint Pipeline, uint Record, ViewFinding Finding), byte> Seen = new();
    private static readonly long[] Counts = new long[Enum.GetValues<ViewFinding>().Length];
    private static long _checks;

    /// <summary>Whether tables are checked (views are noted then too).</summary>
    public static bool Enabled { get; set; }

    /// <summary>Whether views are noted: for the checks or for <see cref="NanScan"/>.</summary>
    public static bool Noting => Enabled || NanScan.Enabled;

    /// <summary>"view liveness: N records checked, findings by reason (distinct)" for the sample line.</summary>
    public static string Summary
    {
        get
        {
            var findings = Enum.GetValues<ViewFinding>().Where(finding => finding != ViewFinding.None)
                .Select(finding => string.Create(CultureInfo.InvariantCulture, $"{Interlocked.Read(ref Counts[(int)finding])} {Describe(finding)}"));

            return $"view liveness: {Interlocked.Read(ref _checks)} records checked, {string.Join(", ", findings)} ({Seen.Count} distinct)";
        }
    }

    /// <summary>The shape of a view written with <paramref name="description"/> (an SRV or UAV desc) over <paramref name="resource"/>.</summary>
    public static ViewShape Shape(nint resource, void* description, bool unordered)
    {
        if (resource == 0)
        {
            return ViewShape.Null;
        }

        bool buffer;

        if (description != null)
        {
            buffer = *(int*)((byte*)description + DimensionOffset) == BufferDimension;
        }
        else
        {
            buffer = IsBuffer(resource);
        }

        if (unordered)
        {
            return Pick(buffer, ViewShape.BufferUav, ViewShape.TextureUav);
        }

        return Pick(buffer, ViewShape.BufferSrv, ViewShape.TextureSrv);
    }

    private static ViewShape Pick(bool buffer, ViewShape bufferShape, ViewShape textureShape)
    {
        if (buffer)
        {
            return bufferShape;
        }

        return textureShape;
    }

    /// <summary>Whether the desktop resource is a buffer (ID3D12Resource::GetDesc); only for a view written without a desc.</summary>
    private static bool IsBuffer(nint resource)
    {
        ResourceDescription description;
        _ = ((delegate* unmanaged<nint, ResourceDescription*, ResourceDescription*>)ComObject.Slot(resource, GetDescSlot))(resource, &description);

        return description.Dimension == ResourceDescription.BufferDimension;
    }

    /// <summary>A view of <paramref name="resource"/> was written at desktop CPU handle <paramref name="handle"/>.</summary>
    [HotPath]
    public static void Written(nint handle, nint resource, ViewShape shape, int home = 0)
    {
        Slots[handle] = new SlotView(resource, shape, home);

        if (resource != 0)
        {
            Live[resource] = 0;
        }
    }

    /// <summary>The slot at <paramref name="handle"/> is written by a path that does not say what: no longer checked.</summary>
    [HotPath]
    public static void Forget(nint handle)
    {
        Slots.TryRemove(handle, out _);
    }

    /// <summary><paramref name="count"/> descriptors copied from <paramref name="source"/> to <paramref name="destination"/>.</summary>
    [HotPath]
    public static void Copied(nint destination, nint source, uint count, uint increment)
    {
        for (uint i = 0; i < count; i++)
        {
            nint to = destination + (nint)((ulong)i * increment);

            if (Slots.TryGetValue(source + (nint)((ulong)i * increment), out var view))
            {
                Slots[to] = view;
            }
            else
            {
                Slots.TryRemove(to, out _);
            }
        }
    }

    /// <summary>What was noted at a desktop CPU handle, and whether its resource is alive; null when nothing is.</summary>
    public static (SlotView View, bool Live)? Peek(nint handle)
    {
        if (!Slots.TryGetValue(handle, out var view))
        {
            return null;
        }

        return (view, view.Resource == 0 || Live.ContainsKey(view.Resource));
    }

    /// <summary>The slots noted as views of any of <paramref name="resources"/>: a scan of every slot, for the texture releases' check.</summary>
    public static List<(nint Handle, SlotView View)> Naming(IReadOnlySet<nint> resources)
    {
        var found = new List<(nint Handle, SlotView View)>();

        foreach (var (handle, view) in Slots)
        {
            if (view.Resource != 0 && resources.Contains(view.Resource))
            {
                found.Add((handle, view));
            }
        }

        return found;
    }

    /// <summary>The desktop object's last reference went: views of it name a released resource.</summary>
    [HotPath]
    public static void Released(nint resource)
    {
        Live.TryRemove(resource, out _);
    }

    /// <summary>Before a draw, dispatch or ExecuteIndirect of <paramref name="list"/>: checks its bound tables.</summary>
    [HotPath]
    public static void Check(XboxObject* list, bool compute)
    {
        var views = ShimContext.Runtime.D3D12X.DescriptorHeaps.Views;
        var bindings = CommandListBindings.Of(list);
        var layout = compute ? bindings->ComputeLayout : bindings->GraphicsLayout;

        if (views is null || layout == null)
        {
            return;
        }

        uint* data = compute ? bindings->Compute : bindings->Graphics;
        uint first = compute ? UserData.ComputeBase : UserData.GraphicsBase;
        nint pipeline = ListActivity.Of(list)->Pipeline;

        for (uint parameter = 0; parameter < (uint)layout->Count; parameter++)
        {
            if (layout->Kind(parameter) == RootParameterKind.Table && first + layout->Offsets[parameter] < CommandListBindings.UserDataDwords)
            {
                CheckTable(views, layout, parameter, data[first + layout->Offsets[parameter]], pipeline);
            }
        }
    }

    /// <summary>What is wrong with a record's view; <see cref="ViewFinding.None"/> for a view that is fine or not known.</summary>
    public static ViewFinding Classify(SlotView? view, bool live, bool uavRange, bool bufferRecord)
    {
        if (view is null || view.Value.Shape == ViewShape.Null)
        {
            return ViewFinding.None;
        }

        var shape = view.Value.Shape;
        bool unordered = shape is ViewShape.BufferUav or ViewShape.TextureUav;
        bool buffer = shape is ViewShape.BufferSrv or ViewShape.BufferUav;

        if (!live)
        {
            return ViewFinding.Released;
        }

        if (uavRange && !unordered)
        {
            return ViewFinding.ReadOnlyInUavRange;
        }

        if (!uavRange && unordered)
        {
            return ViewFinding.UavInReadOnlyRange;
        }

        if (bufferRecord && !buffer)
        {
            return ViewFinding.TextureBehindBufferRecord;
        }

        return ViewFinding.None;
    }

    private static void CheckTable(ShaderVisibleHeap views, RootLayout* layout, uint parameter, uint offset, nint pipeline)
    {
        if (offset >= DescriptorWindow.NullTableBase || offset % DescriptorWindow.ViewRecordBytes != 0)
        {
            return;
        }

        uint index = (offset - DescriptorWindow.ViewBase) / DescriptorWindow.ViewRecordBytes;
        uint extent = layout->Extents[parameter];

        for (uint i = 0; i < extent && index + i < views.Count; i++)
        {
            Interlocked.Increment(ref _checks);
            SlotView? view = null;

            if (Slots.TryGetValue(views.Source(index + i), out var known))
            {
                view = known;
            }

            bool live = view is null || view.Value.Resource == 0 || Live.ContainsKey(view.Value.Resource);
            var finding = Classify(view, live, InUavRange(layout, parameter, i), views.RecordDword(index + i, RecordsDword) != 0);

            if (finding != ViewFinding.None)
            {
                Found(pipeline, parameter, index + i, finding, view!.Value);
            }
        }
    }

    private static bool InUavRange(RootLayout* layout, uint parameter, uint record)
    {
        for (int range = 0; range < layout->UavRanges; range++)
        {
            if (layout->UavParameters[range] == parameter && record >= layout->UavStarts[range] && record < (uint)layout->UavStarts[range] + layout->UavCounts[range])
            {
                return true;
            }
        }

        return false;
    }

    private static void Found(nint pipeline, uint parameter, uint record, ViewFinding finding, SlotView view)
    {
        Interlocked.Increment(ref Counts[(int)finding]);

        if (!Seen.TryAdd((pipeline, record, finding), 0) || Seen.Count > LoggedFindings)
        {
            return;
        }

        ShimContext.Runtime.Log.Warn($"[view liveness] {QueueProgress.PipelineName(pipeline)}: table parameter {parameter}, record {record}: {Describe(finding)} (resource 0x{view.Resource:x}, {view.Shape})");
    }

    private static string Describe(ViewFinding finding)
    {
        return finding switch
        {
            ViewFinding.Released => "views of a released resource",
            ViewFinding.UavInReadOnlyRange => "UAVs in an SRV/CBV range",
            ViewFinding.ReadOnlyInUavRange => "SRVs in a UAV range",
            ViewFinding.TextureBehindBufferRecord => "texture views behind a buffer record",
            _ => "none",
        };
    }
}
