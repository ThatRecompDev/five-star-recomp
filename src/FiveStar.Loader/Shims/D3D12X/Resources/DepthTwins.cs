// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Binding;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// <c>--depth-twins</c>: the console writes depth textures through UAVs (the tiled lighting writes each pixel's
/// material class into the main depth, then draws each class with depth EQUAL), which the desktop refuses. A depth
/// plane UAV goes to a colour twin of the texture instead (R32_FLOAT for D32, R16_UNORM for D16), which rests in
/// UNORDERED_ACCESS; <see cref="DepthTwinCopy"/> writes the twin back into the depth before a draw that tests against
/// it. A stencil plane UAV (R8_UINT, or a stencil plane format) goes to an R8_UINT twin the same way: the classify
/// pass writes each pixel's material mask there, and the lighting reads it back through the stencil plane's SRV. The
/// design and the facts behind it follow VIctory's depth twins (GPL-3.0, facts only).
/// </summary>
internal static unsafe class DepthTwins
{
    /// <summary>D3D12_RESOURCE_STATE_UNORDERED_ACCESS, where a twin rests.</summary>
    public const int TwinState = 0x8;

    /// <summary>
    /// The view formats whose plane gets a twin: R32_FLOAT (the depth of D32 and D32S8), R16_UNORM and R16_UINT (D16;
    /// the material classes as whole steps), and the console's R16_UNORM_X8_TYPELESS (the depth of D16S8, which the
    /// desktop makes D32S8: its twin is R16_UNORM).
    /// </summary>
    public const int Float32 = 41;
    public const int Unorm16 = 56;
    public const int Uint16 = 57;
    public const int ConsoleUnorm16 = 119;

    /// <summary>R8_UINT: the stencil twin's format, and the UAV format the game gives a stencil plane.</summary>
    public const int Uint8 = 62;

    /// <summary>X32_TYPELESS_G8X24_UINT, X24_TYPELESS_G8_UINT and the console's X16_TYPELESS_G8_UINT: stencil plane views.</summary>
    public const int StencilX32 = 22;
    public const int StencilX24 = 47;
    public const int StencilX16 = 120;

    private const int TextureDimension2D = 3;
    private const int DefaultHeap = 1;

    private static readonly Guid ResourceIid = new("696442be-a72e-4059-bc79-5b5c98040fad");
    private static readonly ConcurrentDictionary<(nint Depth, bool Stencil), DepthTwin> Twins = new();
    private static readonly ConcurrentDictionary<nint, (nint Depth, int Format)> DepthViews = new();
    private static readonly ConcurrentDictionary<uint, DepthTwin> Indices = new();

    /// <summary>D3D12_UNORDERED_ACCESS_VIEW_DESC: Texture2D.PlaneSlice after Format, ViewDimension and MipSlice.</summary>
    public const int PlaneSliceOffset = 12;

    public static bool Enabled { get; set; }

    /// <summary>Whether twins are on and any depth texture has one (twins a test made with the option off do not count).</summary>
    public static bool Any => Enabled && !Twins.IsEmpty;

    /// <summary>Whether a UAV of <paramref name="viewFormat"/> on a depth texture takes a twin.</summary>
    public static bool Twinned(int viewFormat)
    {
        return viewFormat is Float32 or Unorm16 or Uint16 or ConsoleUnorm16 || IsStencil(viewFormat);
    }

    /// <summary>Whether a view of <paramref name="viewFormat"/> on a depth texture is of its stencil plane.</summary>
    public static bool IsStencil(int viewFormat)
    {
        return viewFormat is Uint8 or StencilX32 or StencilX24 or StencilX16;
    }

    /// <summary>
    /// The format of the twin a UAV of <paramref name="viewFormat"/> writes: R8_UINT for the stencil plane, R16_UNORM for
    /// the console's 16-bit depth view, else the view's.
    /// </summary>
    public static int TwinFormat(int viewFormat)
    {
        if (IsStencil(viewFormat))
        {
            return Uint8;
        }

        if (viewFormat == ConsoleUnorm16)
        {
            return Unorm16;
        }

        return viewFormat;
    }

    /// <summary>
    /// The twin of the plane of the desktop depth texture <paramref name="depth"/> a UAV of <paramref name="viewFormat"/>
    /// writes (made on first use); null when the device refuses it.
    /// </summary>
    public static DepthTwin? For(nint device, nint depth, ResourceDescription desc, int viewFormat)
    {
        var key = (depth, IsStencil(viewFormat));

        if (Twins.TryGetValue(key, out var twin))
        {
            return twin;
        }

        var description = new ResourceDescription
        {
            Dimension = TextureDimension2D,
            Width = desc.Width,
            Height = desc.Height,
            DepthOrArraySize = desc.DepthOrArraySize,
            MipLevels = desc.MipLevels,
            Format = TwinFormat(viewFormat),
            SampleCount = 1,
            Flags = DesktopCalls.UnorderedAccessFlag,
        };
        var heap = new HeapProperties { Type = DefaultHeap };
        Guid iid = ResourceIid;
        nint resource = 0;
        var create = (delegate* unmanaged<nint, HeapProperties*, int, ResourceDescription*, int, void*, Guid*, nint*, int>)ComObject.Slot(device, DesktopCalls.CreateCommittedResourceSlot);

        if (HResult.Failed(create(device, &heap, 0, &description, TwinState, null, &iid, &resource)))
        {
            return null;
        }

        twin = new DepthTwin(depth, resource, description.Format, (uint)desc.Width, desc.Height);
        var added = Twins.GetOrAdd(key, twin);

        if (added == twin)
        {
            ShimContext.Log?.Info($"  [d3d12_xs] depth twins: twin 0x{resource:x} in format {twin.Format} for depth 0x{depth:x} ({twin.Width}x{twin.Height}, a UAV in format {viewFormat})");
        }

        return added;
    }

    /// <summary>Forgets every twin and view (their textures belong to the device they were made on): for tests, which make a device each.</summary>
    internal static void Detach()
    {
        Twins.Clear();
        DepthViews.Clear();
        Indices.Clear();
    }

    /// <summary>
    /// Views were written at desktop heap indices <paramref name="first"/> to <paramref name="first"/> + <paramref name="count"/>:
    /// a twin's UAV that was there is gone (the game reuses heap indices), so its index no longer names the twin and the
    /// twin's handle no longer reads it.
    /// </summary>
    [HotPath]
    public static void Overwritten(uint first, uint count)
    {
        if (Indices.IsEmpty)
        {
            return;
        }

        for (uint index = first; index < first + count; index++)
        {
            if (Indices.TryRemove(index, out var twin) && twin.ViewIndex == index)
            {
                twin.ViewHandle = 0;
            }
        }
    }

    /// <summary>The depth plane's twin of a desktop depth texture; null without one.</summary>
    public static DepthTwin? Of(nint depth)
    {
        return Twins.GetValueOrDefault((depth, false));
    }

    /// <summary>The stencil plane's twin of a desktop depth texture; null without one.</summary>
    public static DepthTwin? StencilOf(nint depth)
    {
        return Twins.GetValueOrDefault((depth, true));
    }

    /// <summary>A depth-stencil view of <paramref name="depth"/> in <paramref name="format"/> was written at desktop CPU handle <paramref name="handle"/>.</summary>
    public static void NoteDepthView(nint handle, nint depth, int format)
    {
        DepthViews[handle] = (depth, format);
    }

    /// <summary>
    /// The depth plane's twin behind a bound depth-stencil view and the view's format; null when the view's texture has
    /// none. <paramref name="stencil"/> is the stencil plane's twin, null without one.
    /// </summary>
    public static DepthTwin? OfView(nint handle, out int format, out DepthTwin? stencil)
    {
        format = 0;
        stencil = null;

        if (handle == 0 || !DepthViews.TryGetValue(handle, out var view))
        {
            return null;
        }

        format = view.Format;
        stencil = StencilOf(view.Depth);

        return Of(view.Depth);
    }

    /// <summary>
    /// The twin's UAV went to desktop CPU handle <paramref name="handle"/> of the shader-visible view heap. Its index there
    /// is what the window records name (a record's dword 0), which the writers' scan reads; the game's own heap index
    /// differs once the window remaps it.
    /// </summary>
    public static void NoteView(DepthTwin twin, nint handle)
    {
        var d3d12x = ShimContext.Runtime.D3D12X;
        nint heap = d3d12x.Bindings.Window.ViewHeap;
        NoteView(twin, handle, heap, DesktopCalls.HeapCpuStart(heap), d3d12x.DescriptorHeaps.Views?.Increment ?? 1);
    }

    /// <summary>As <see cref="NoteView(DepthTwin, nint)"/> in the view heap <paramref name="heap"/> starting at CPU handle <paramref name="start"/>.</summary>
    public static void NoteView(DepthTwin twin, nint handle, nint heap, nint start, uint increment)
    {
        uint index = (uint)((ulong)(handle - start) / increment);
        twin.ViewHandle = handle;
        twin.ViewHeap = heap;
        twin.ViewIndex = index;
        Indices[index] = twin;
    }

    /// <summary>The twin whose UAV the view heap holds at desktop index <paramref name="index"/>; null for none.</summary>
    public static DepthTwin? AtIndex(uint index)
    {
        return Indices.GetValueOrDefault(index);
    }

    /// <summary>
    /// Before a draw or dispatch on <paramref name="self"/>: the twins whose UAVs the tables it binds hold (by the heap
    /// indices in the window records its programs reach, read anew each time, as the tables and their records change)
    /// are noted as written by the list (<see cref="DepthTwinSides"/>).
    /// </summary>
    [HotPath]
    public static void MarkWriters(XboxObject* self, uint* userData, byte* window, TableReachEntry[] tables)
    {
        if (window == null)
        {
            return;
        }

        foreach (var entry in tables)
        {
            if (entry.Sgpr >= CommandListBindings.UserDataDwords)
            {
                continue;
            }

            uint table = userData[entry.Sgpr];

            for (uint i = 0; i < entry.Records && (ulong)table + ((i + 1UL) * DescriptorWindow.ViewRecordBytes) <= DescriptorWindow.RecordsEnd; i++)
            {
                uint? index = DebugLayer.DepthUavs.HeapIndex((uint*)(window + table + (i * DescriptorWindow.ViewRecordBytes)));

                if (index.HasValue && Indices.TryGetValue(index.Value, out var twin))
                {
                    Written(self, twin);
                }
            }
        }
    }

    /// <summary>
    /// A pass on <paramref name="self"/> writes <paramref name="twin"/> (<see cref="DepthTwinSides.UseTwin"/>); when the
    /// list wrote the plane since its last use of the twin, the plane is copied into the twin first, here.
    /// </summary>
    [HotPath]
    private static void Written(XboxObject* self, DepthTwin twin)
    {
        if (twin.TakeFirstWriter())
        {
            NoteFirstWriter(self, twin);
        }

        if (DepthTwinSides.UseTwin((nint)self, twin))
        {
            Fill(self, twin);
        }
    }

    private static void NoteFirstWriter(XboxObject* self, DepthTwin twin)
    {
        string pipeline = QueueProgress.PipelineName(RectangleListState.Of(self)->Pipeline);
        ShimContext.Log?.Info($"  [d3d12_xs] depth twins: {pipeline} writes twin 0x{twin.Twin:x} (format {twin.Format}, depth 0x{twin.Depth:x}, {twin.Width}x{twin.Height}) first");
    }

    /// <summary>The plane into the twin on the list; warned once when this list cannot (a compute list).</summary>
    private static void Fill(XboxObject* self, DepthTwin twin)
    {
        var d3d12x = ShimContext.Runtime.D3D12X;
        var states = d3d12x.ListStates.For(self);
        int state = states.Current(twin.Depth, twin.Home);

        if (DepthTwinFill.Record(d3d12x.Objects.DesktopDevice, self->Inner, twin, state, states.LegalStates))
        {
            DepthTwinLog.InList(false, twin);
        }
        else
        {
            d3d12x.Warnings.Warn("depth-twin-fill", $"[d3d12_xs] depth twins: a list wrote a depth plane and then a pass that writes its twin (format {twin.Format}), and cannot copy the plane into the twin in between (a compute list, or the formats differ)");
        }
    }
}
