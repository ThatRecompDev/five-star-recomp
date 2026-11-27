// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Graphics.Replay;
using FiveStar.Loader.Shims.D3D12X.Binding;
using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// The view behind a desktop heap index, for a capture (<see cref="CapturePlanner"/>): from what <see cref="ViewLiveness"/>
/// noted at its handle, a texture whole (every subresource) or a buffer view's range of its buffer, the view's desc
/// moved to the range's start. A slot with no live view becomes a null raw SRV.
/// </summary>
internal static unsafe class CaptureViews
{
    /// <summary>Bytes a capture keeps at most, before and after together, and of one texture or buffer view.</summary>
    public const ulong MaxSourceBytes = 768UL << 20;
    private const ulong MaxTextureBytes = 256UL << 20;
    private const ulong MaxBufferViewBytes = 64UL << 20;

    /// <summary>A source is copied twice: before the dispatch and after it.</summary>
    private const ulong BeforeAndAfter = 2;

    /// <summary>Bytes of a raw view's element, and the most a typed view's element can be (R32G32B32A32).</summary>
    private const uint RawElementBytes = 4;
    private const uint TypedElementBytes = 16;

    /// <summary>D3D12_UNORDERED_ACCESS_VIEW_DESC: Buffer.CounterOffsetInBytes (d3d12.h).</summary>
    private const int CounterOffsetField = 24;

    /// <summary>The descriptor at desktop <paramref name="index"/> (CPU handle <paramref name="handle"/>) and what it views.</summary>
    public static void View(XboxObject* list, CapturePlan plan, uint index, nint handle)
    {
        var noted = ViewLiveness.Peek(handle);

        if (noted is null || !noted.Value.Live || noted.Value.View.Resource == 0 || noted.Value.View.Shape == ViewShape.Null)
        {
            plan.Views.Add(new PlannedView(index, CapturedViewKind.ShaderResource, null, NullRaw()));

            return;
        }

        var view = noted.Value.View;
        bool unordered = view.Shape is ViewShape.BufferUav or ViewShape.TextureUav;
        var description = ViewLiveness.Description(handle) ?? [];
        CaptureSource? source;

        if (view.Shape is ViewShape.TextureSrv or ViewShape.TextureUav)
        {
            source = Texture(plan, view);
        }
        else
        {
            source = BufferView(plan, index, view.Resource, ref description, unordered);
        }

        if (source is null)
        {
            plan.Views.Add(new PlannedView(index, CapturedViewKind.ShaderResource, null, NullRaw()));

            return;
        }

        source.Output |= unordered;
        var kind = unordered ? CapturedViewKind.UnorderedAccess : CapturedViewKind.ShaderResource;
        plan.Views.Add(new PlannedView(index, kind, source.Shape.Id, description));
    }

    /// <summary>A null raw SRV desc.</summary>
    public static byte[] NullRaw()
    {
        return Bytes(BufferViewDescription.RawShaderResource(0));
    }

    public static byte[] Bytes(BufferViewDescription description)
    {
        return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref description, 1)).ToArray();
    }

    /// <summary>A desktop texture, every subresource, once per capture; null for a multisampled or too large one.</summary>
    private static CaptureSource? Texture(CapturePlan plan, SlotView view)
    {
        if (plan.Textures.TryGetValue(view.Resource, out var known))
        {
            return known;
        }

        Marshal.AddRef(view.Resource);
        using var texture = new ID3D12Resource(view.Resource);
        var desc = texture.Description;

        nint device = ShimContext.Runtime.D3D12X.Objects.DesktopDevice;
        Marshal.AddRef(device);
        using var desktop = new ID3D12Device(device);
        uint slices = desc.DepthOrArraySize;

        // A 3D texture's depth is one subresource per mip.
        if (desc.Dimension == ResourceDimension.Texture3D)
        {
            slices = 1;
        }

        uint subresources = desc.MipLevels * slices;
        var footprints = new PlacedSubresourceFootPrint[subresources];
        var rows = new uint[subresources];
        var rowBytes = new ulong[subresources];
        desktop.GetCopyableFootprints(desc, 0, subresources, 0, footprints, rows, rowBytes, out ulong total);

        if (desc.SampleDescription.Count > 1 || total > MaxTextureBytes || plan.SourceBytes + (BeforeAndAfter * total) > MaxSourceBytes)
        {
            plan.Notes.Add($"texture 0x{view.Resource:x} ({desc.Width}x{desc.Height}, format {desc.Format}, {total} bytes) not kept: multisampled or past the cap");

            return null;
        }

        var captured = footprints.Select((footprint, at) => new CapturedFootprint(
            footprint.Offset, (int)footprint.Footprint.Format, footprint.Footprint.Width, footprint.Footprint.Height, footprint.Footprint.Depth, footprint.Footprint.RowPitch, rows[at], rowBytes[at])).ToList();
        var shape = new CapturedResource($"texture-{plan.Sources.Count}", $"desktop texture 0x{view.Resource:x}", (int)desc.Dimension, desc.Width, desc.Height, desc.DepthOrArraySize, desc.MipLevels, (int)desc.Format, (int)desc.Flags, total, null, null)
        {
            Footprints = captured,
        };
        var source = new CaptureSource(shape, [], view.Resource, view.Home);
        Add(plan, source);
        plan.Textures[view.Resource] = source;

        return source;
    }

    /// <summary>The range of its buffer a buffer view covers, its desc moved to start at the range; null past the cap.</summary>
    private static CaptureSource? BufferView(CapturePlan plan, uint index, nint resource, ref byte[] description, bool unordered)
    {
        Marshal.AddRef(resource);
        using var buffer = new ID3D12Resource(resource);
        ulong width = buffer.Description.Width;
        ulong start = 0;
        ulong bytes = width;

        if (description.Length == BufferViewDescription.Size)
        {
            var view = MemoryMarshal.Read<BufferViewDescription>(description);

            if (view.Dimension != BufferViewDescription.BufferDimension)
            {
                return null;
            }

            (start, bytes) = Range(ref view, unordered, width);
            description = Bytes(view);

            if (unordered)
            {
                description.AsSpan(CounterOffsetField, sizeof(ulong)).Clear();
            }
        }

        if (bytes == 0 || bytes > MaxBufferViewBytes || plan.SourceBytes + (BeforeAndAfter * bytes) > MaxSourceBytes)
        {
            return null;
        }

        var shape = new CapturedResource($"view-{index}", $"buffer view {index}: 0x{resource:x} +0x{start:x}", CapturedResource.BufferDimension, bytes, 1, 1, 1, 0, 0, bytes, null, null);
        var source = new CaptureSource(shape, [new CapturePiece(resource, start, 0, bytes)], 0, 0);
        Add(plan, source);

        return source;
    }

    /// <summary>A buffer view's byte range of its buffer, the view moved to element 0 of it.</summary>
    private static (ulong Start, ulong Bytes) Range(ref BufferViewDescription view, bool unordered, ulong width)
    {
        ulong first = unordered ? view.UavFirstElement : view.SrvFirstElement;
        uint elements = unordered ? view.UavElements : view.SrvElements;
        uint stride = unordered ? view.UavStride : view.SrvStride;
        int flags = unordered ? view.UavFlags : view.SrvFlags;
        ulong element = TypedElementBytes;

        if ((flags & BufferViewDescription.RawFlag) != 0)
        {
            element = RawElementBytes;
        }
        else if (stride != 0)
        {
            element = stride;
        }

        ulong start = Math.Min(first * element, width);
        ulong bytes = Math.Min((ulong)elements * element, width - start);

        if (unordered)
        {
            view.UavFirstElement = 0;
        }
        else
        {
            view.SrvFirstElement = 0;
        }

        return (start, bytes);
    }

    private static void Add(CapturePlan plan, CaptureSource source)
    {
        plan.Sources.Add(source);
        plan.SourceBytes += source.Shape.Bytes * BeforeAndAfter;
    }
}
