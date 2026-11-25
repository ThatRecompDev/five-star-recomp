// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// Copies a depth texture's plane into its twin before the first pass that writes the twin (<see cref="DepthTwins"/>),
/// so what that pass leaves alone keeps the plane's values instead of zeros. A depth-stencil plane copies only through
/// a buffer: the plane goes into a buffer of its footprint, which goes into the twin as the twin's format. Mip 0 of the
/// first slice, as the copy back (<see cref="DepthTwinCopy"/>). Only when the plane and the twin have the same bytes a
/// texel (not a 32-bit depth plane under a 16-bit twin) and the list can name the depth's state (a direct list).
/// </summary>
internal static class DepthTwinFill
{
    /// <summary>D3D12_RESOURCE_STATE_UNORDERED_ACCESS, where a twin rests.</summary>
    private const ResourceStates TwinRest = (ResourceStates)DepthTwins.TwinState;

    /// <summary>Bytes a texel of the footprint and twin formats this copies: R32 (39, 41), R16 (53, 56, 57), R8 (60, 62).</summary>
    private static readonly Dictionary<int, int> TexelBytes = new()
    {
        [39] = sizeof(float), [41] = sizeof(float),
        [53] = sizeof(ushort), [56] = sizeof(ushort), [57] = sizeof(ushort),
        [60] = sizeof(byte), [62] = sizeof(byte),
    };

    /// <summary>
    /// Records the copy of <paramref name="twin"/>'s plane into the twin on the desktop <paramref name="list"/>, where the
    /// depth is in <paramref name="state"/>; false when this list or these formats cannot.
    /// </summary>
    public static bool Record(nint device, nint list, DepthTwin twin, int state, int legalStates)
    {
        if ((state & ~legalStates) != 0)
        {
            return false;
        }

        Marshal.AddRef(device);
        using var desktop = new ID3D12Device(device);
        Marshal.AddRef(twin.Depth);
        using var depth = new ID3D12Resource(twin.Depth);
        Marshal.AddRef(twin.Twin);
        using var copy = new ID3D12Resource(twin.Twin);

        var description = depth.Description;
        int plane = twin.Stencil ? 1 : 0;
        uint subresource = DrawProbes.PlaneSubresource(plane, description.MipLevels, description.DepthOrArraySize);

        var footprints = new PlacedSubresourceFootPrint[1];
        var rows = new uint[1];
        var rowBytes = new ulong[1];
        desktop.GetCopyableFootprints(description, subresource, 1, 0, footprints, rows, rowBytes, out ulong total);

        if (!SameTexel((int)footprints[0].Footprint.Format, twin.Format))
        {
            return false;
        }

        var buffer = desktop.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None, Vortice.Direct3D12.ResourceDescription.Buffer(total), ResourceStates.Common);
        twin.FillBuffer?.Dispose();
        twin.FillBuffer = buffer;

        Marshal.AddRef(list);
        using var commands = new ID3D12GraphicsCommandList(list);
        Copy(commands, depth, copy, buffer, footprints[0], subresource, (ResourceStates)state, (Vortice.DXGI.Format)twin.Format);

        return true;
    }

    private static void Copy(ID3D12GraphicsCommandList commands, ID3D12Resource depth, ID3D12Resource twin, ID3D12Resource buffer, PlacedSubresourceFootPrint footprint, uint subresource, ResourceStates resting, Vortice.DXGI.Format twinFormat)
    {
        var asTwin = footprint;
        asTwin.Footprint.Format = twinFormat;

        commands.ResourceBarrierTransition(depth, resting, ResourceStates.CopySource);
        commands.CopyTextureRegion(new TextureCopyLocation(buffer, footprint), 0, 0, 0, new TextureCopyLocation(depth, subresource), null);
        commands.ResourceBarrierTransition(buffer, ResourceStates.CopyDest, ResourceStates.CopySource);
        commands.ResourceBarrierTransition(twin, TwinRest, ResourceStates.CopyDest);
        commands.CopyTextureRegion(new TextureCopyLocation(twin, 0), 0, 0, 0, new TextureCopyLocation(buffer, asTwin), null);

        commands.ResourceBarrierTransition(twin, ResourceStates.CopyDest, TwinRest);
        commands.ResourceBarrierTransition(depth, ResourceStates.CopySource, resting);
    }

    /// <summary>Whether a texel of the plane's footprint <paramref name="footprint"/> is one of the twin's (<paramref name="twin"/>).</summary>
    public static bool SameTexel(int footprint, int twin)
    {
        return TexelBytes.TryGetValue(footprint, out int plane) && TexelBytes.TryGetValue(twin, out int texel) && plane == texel;
    }
}
