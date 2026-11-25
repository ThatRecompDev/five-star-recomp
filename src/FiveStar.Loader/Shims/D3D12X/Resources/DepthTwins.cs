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

    /// <summary>The view formats whose plane gets a twin: R32_FLOAT (the depth of D32 and D32S8), R16_UNORM (D16).</summary>
    public const int Float32 = 41;
    public const int Unorm16 = 56;

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
    private static readonly ConcurrentDictionary<nint, DepthTwin[]> Writers = new();

    /// <summary>D3D12_UNORDERED_ACCESS_VIEW_DESC: Texture2D.PlaneSlice after Format, ViewDimension and MipSlice.</summary>
    public const int PlaneSliceOffset = 12;

    public static bool Enabled { get; set; }

    /// <summary>Whether any depth texture has a twin.</summary>
    public static bool Any => !Twins.IsEmpty;

    /// <summary>Whether a UAV of <paramref name="viewFormat"/> on a depth texture takes a twin.</summary>
    public static bool Twinned(int viewFormat)
    {
        return viewFormat is Float32 or Unorm16 || IsStencil(viewFormat);
    }

    /// <summary>Whether a view of <paramref name="viewFormat"/> on a depth texture is of its stencil plane.</summary>
    public static bool IsStencil(int viewFormat)
    {
        return viewFormat is Uint8 or StencilX32 or StencilX24 or StencilX16;
    }

    /// <summary>The format of the twin a UAV of <paramref name="viewFormat"/> writes: R8_UINT for the stencil plane, else the view's.</summary>
    public static int TwinFormat(int viewFormat)
    {
        if (IsStencil(viewFormat))
        {
            return Uint8;
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

        return Twins.GetOrAdd(key, twin);
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

    /// <summary>The twin's UAV went to game heap index <paramref name="index"/>, desktop CPU handle <paramref name="handle"/>.</summary>
    public static void NoteView(uint index, DepthTwin twin, nint handle)
    {
        twin.ViewHandle = handle;
        Indices[index] = twin;
    }

    /// <summary>
    /// Before a draw or dispatch of <paramref name="pipeline"/>: the twins whose UAVs its tables hold (found at its first
    /// use, by the heap indices in the window records its programs reach) are marked written.
    /// </summary>
    public static void MarkWriters(nint pipeline, uint* userData, byte* window, TableReachEntry[] tables)
    {
        if (!Writers.TryGetValue(pipeline, out var twins))
        {
            twins = Find(userData, window, tables);
            Writers[pipeline] = twins;
        }

        foreach (var twin in twins)
        {
            twin.MarkWritten();
        }
    }

    private static DepthTwin[] Find(uint* userData, byte* window, TableReachEntry[] tables)
    {
        var found = new List<DepthTwin>();

        if (window == null)
        {
            return [];
        }

        foreach (var entry in tables)
        {
            if (entry.Sgpr >= CommandListBindings.UserDataDwords)
            {
                continue;
            }

            uint table = userData[entry.Sgpr];

            for (uint i = 0; i < entry.Records && table + ((i + 1) * DescriptorWindow.ViewRecordBytes) <= DescriptorWindow.RecordsEnd; i++)
            {
                uint? index = DebugLayer.DepthUavs.HeapIndex((uint*)(window + table + (i * DescriptorWindow.ViewRecordBytes)));

                if (index.HasValue && Indices.TryGetValue(index.Value, out var twin) && !found.Contains(twin))
                {
                    found.Add(twin);
                }
            }
        }

        return [.. found];
    }
}
