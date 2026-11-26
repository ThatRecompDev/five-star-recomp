// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// The GPU copies of a capture, recorded in the game's list around the dispatch (as <see cref="DrawProbes"/> and
/// <see cref="NanScan"/> read textures back): after a UAV barrier, every source into one readback buffer before the
/// dispatch, and each output again after it; an ExecuteIndirect's argument records and count before it. A texture is
/// copied from its state on that list and put back; buffers are copied in the state they are in (no transition).
/// </summary>
internal static unsafe class CaptureCopies
{
    /// <summary>D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT: where each copy starts in the readback buffer.</summary>
    private const ulong Alignment = 512;
    private const ulong CountBytes = sizeof(uint);

    /// <summary>Places every copy in the readback buffer and sets its size.</summary>
    public static void Layout(CapturePlan plan)
    {
        ulong at = 0;

        foreach (var source in plan.Sources)
        {
            source.Before = at;
            at = Align(at + source.Shape.Bytes);

            if (source.Output)
            {
                source.After = at;
                at = Align(at + source.Shape.Bytes);
            }
        }

        if (plan.Indirect is not null)
        {
            plan.Indirect.At = at;
            at = Align(at + plan.Indirect.Bytes + CountBytes);
        }

        plan.ReadbackBytes = Math.Max(at, Alignment);
    }

    /// <summary>The readback buffer of a laid-out plan.</summary>
    public static ID3D12Resource Readback(CapturePlan plan)
    {
        nint device = ShimContext.Runtime.D3D12X.Objects.DesktopDevice;
        Marshal.AddRef(device);
        using var desktop = new ID3D12Device(device);

        return desktop.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None, Vortice.Direct3D12.ResourceDescription.Buffer(plan.ReadbackBytes), ResourceStates.CopyDest);
    }

    /// <summary>Records the copies before the dispatch, or with <paramref name="after"/> those of its outputs after it.</summary>
    public static void Record(XboxObject* list, CapturePlan plan, ID3D12Resource readback, bool after)
    {
        Marshal.AddRef(list->Inner);
        using var commands = new ID3D12GraphicsCommandList(list->Inner);
        commands.ResourceBarrier(ResourceBarrier.BarrierUnorderedAccessView(null!));

        foreach (var source in plan.Sources)
        {
            if (after && !source.Output)
            {
                continue;
            }

            ulong at = after ? source.After : source.Before;

            if (source.Texture == 0)
            {
                CopyBuffer(commands, source, readback, at);
            }
            else
            {
                CopyTexture(commands, list, source, readback, at);
            }
        }

        if (!after && plan.Indirect is not null)
        {
            CopyArguments(commands, plan.Indirect, readback);
        }
    }

    private static void CopyBuffer(ID3D12GraphicsCommandList commands, CaptureSource source, ID3D12Resource readback, ulong at)
    {
        foreach (var piece in source.Pieces)
        {
            using var buffer = Wrap(piece.Buffer);
            commands.CopyBufferRegion(readback, at + piece.TargetOffset, buffer, piece.SourceOffset, piece.Bytes);
        }
    }

    /// <summary>Every subresource from the texture's state on the list, which it goes back to.</summary>
    private static void CopyTexture(ID3D12GraphicsCommandList commands, XboxObject* list, CaptureSource source, ID3D12Resource readback, ulong at)
    {
        var resting = (ResourceStates)(ShimContext.Runtime.D3D12X.ListStates.Find(list)?.Current(source.Texture, source.Home) ?? source.Home);
        using var texture = Wrap(source.Texture);
        var footprints = source.Shape.Footprints ?? [];

        if (resting != ResourceStates.CopySource)
        {
            commands.ResourceBarrierTransition(texture, resting, ResourceStates.CopySource);
        }

        for (var subresource = 0; subresource < footprints.Count; subresource++)
        {
            var footprint = footprints[subresource];
            var placed = new PlacedSubresourceFootPrint
            {
                Offset = at + footprint.Offset,
                Footprint = new SubresourceFootPrint((Vortice.DXGI.Format)footprint.Format, footprint.Width, footprint.Height, footprint.Depth, footprint.RowPitch),
            };
            commands.CopyTextureRegion(new TextureCopyLocation(readback, placed), 0, 0, 0, new TextureCopyLocation(texture, (uint)subresource), null);
        }

        if (resting != ResourceStates.CopySource)
        {
            commands.ResourceBarrierTransition(texture, ResourceStates.CopySource, resting);
        }
    }

    private static void CopyArguments(ID3D12GraphicsCommandList commands, IndirectArguments arguments, ID3D12Resource readback)
    {
        using (var records = Wrap(arguments.Buffer))
        {
            commands.CopyBufferRegion(readback, arguments.At, records, arguments.Offset, arguments.Bytes);
        }

        if (arguments.CountBuffer != 0)
        {
            using var count = Wrap(arguments.CountBuffer);
            commands.CopyBufferRegion(readback, arguments.At + arguments.Bytes, count, arguments.CountOffset, CountBytes);
        }
    }

    private static ID3D12Resource Wrap(nint resource)
    {
        Marshal.AddRef(resource);

        return new ID3D12Resource(resource);
    }

    private static ulong Align(ulong value)
    {
        return (value + Alignment - 1) & ~(Alignment - 1);
    }
}
