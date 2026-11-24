// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Formats.Images;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Shims.D3D12X.Resources;
using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// Debug readback of a render target right after a draw, inside the game's own command list: the target goes from the
/// state it is in on that list to COPY_SOURCE, is copied into a readback buffer and goes back, so the copy sees exactly
/// what the draw left. <see cref="Report"/> maps the buffers once the GPU finished (the caller waited) and logs what
/// each holds. The game executes some closed lists more than once, so a buffer is released only at a later report
/// after the list it was copied on has been reset (the GPU is idle then and no execution can name it any more). Not for
/// per-frame use: each probe makes a buffer of the target's size.
/// </summary>
internal sealed unsafe class DrawProbes
{
    private readonly object _gate = new();
    private readonly List<(string Label, ID3D12Resource Buffer, PlacedSubresourceFootPrint Footprint, uint Rows, ulong RowBytes, int Width, int Format, nint List, long Generation, string? Png)> _pending = [];
    private readonly List<(ID3D12Resource Buffer, nint List, long Generation)> _reported = [];
    private readonly LoaderLog _log;

    public DrawProbes(LoaderLog log)
    {
        _log = log;
    }

    /// <summary>Records the copy of <paramref name="target"/> (in <paramref name="state"/> on the list) into the desktop list <paramref name="list"/>.</summary>
    /// <paramref name="key"/> and <paramref name="generation"/> name the game's list and how often it was reset; with
    /// <paramref name="png"/> the report also writes the image there.
    public void Record(nint device, nint list, nint target, int state, string label, nint key, long generation, string? png = null)
    {
        Marshal.AddRef(device);
        using var desktop = new ID3D12Device(device);
        Marshal.AddRef(target);
        var texture = new ID3D12Resource(target);
        var description = texture.Description;

        // A multisampled or non-2D target cannot be copied to a buffer this way.
        if (description.Dimension != ResourceDimension.Texture2D || description.SampleDescription.Count > 1)
        {
            texture.Dispose();

            return;
        }

        var footprints = new PlacedSubresourceFootPrint[1];
        var rows = new uint[1];
        var rowBytes = new ulong[1];
        desktop.GetCopyableFootprints(description, 0, 1, 0, footprints, rows, rowBytes, out ulong total);
        var buffer = desktop.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None, Vortice.Direct3D12.ResourceDescription.Buffer(total), ResourceStates.CopyDest);

        Marshal.AddRef(list);
        using var commands = new ID3D12GraphicsCommandList(list);
        var resting = (ResourceStates)state;

        commands.ResourceBarrierTransition(texture, resting, ResourceStates.CopySource);
        commands.CopyTextureRegion(new TextureCopyLocation(buffer, footprints[0]), 0, 0, 0, new TextureCopyLocation(texture, 0), null);
        commands.ResourceBarrierTransition(texture, ResourceStates.CopySource, resting);
        texture.Dispose();

        lock (_gate)
        {
            _pending.Add((label, buffer, footprints[0], rows[0], rowBytes[0], (int)description.Width, (int)description.Format, key, generation, png));
        }
    }

    /// <summary>
    /// Logs what every recorded probe holds; the GPU has finished the lists that recorded them. Buffers of lists reset
    /// since their probe (<paramref name="generation"/> of a list key) are released.
    /// </summary>
    public void Report(Func<nint, long> generation)
    {
        lock (_gate)
        {
            for (int i = _reported.Count - 1; i >= 0; i--)
            {
                if (generation(_reported[i].List) != _reported[i].Generation)
                {
                    _reported[i].Buffer.Dispose();
                    _reported.RemoveAt(i);
                }
            }

            foreach (var probe in _pending)
            {
                _log.Info($"  [draws] after {probe.Label}: {Summary(probe.Buffer, probe.Footprint, probe.Rows, probe.RowBytes, probe.Width, probe.Format, probe.Png)}");
                _reported.Add((probe.Buffer, probe.List, probe.Generation));
            }

            _pending.Clear();
        }
    }

    private static string Summary(ID3D12Resource buffer, PlacedSubresourceFootPrint footprint, uint rows, ulong rowBytes, int width, int format, string? png)
    {
        var pixels = new byte[(int)rowBytes * (int)rows];
        void* data;
        buffer.Map(0, null, &data).CheckError();

        for (int row = 0; row < rows; row++)
        {
            new ReadOnlySpan<byte>((byte*)data + (row * footprint.Footprint.RowPitch), (int)rowBytes).CopyTo(pixels.AsSpan(row * (int)rowBytes));
        }

        buffer.Unmap(0);
        var rgba = ReadbackPixels.ToRgba(new ReadbackImage(width, (int)rows, format, (int)rowBytes, pixels));

        if (rgba is null)
        {
            return $"format {format} not converted";
        }

        if (png is not null)
        {
            PngWriter.Write(png, width, (int)rows, rgba);
        }

        return ReadbackPixels.Summarize(rgba, width, (int)rows);
    }
}
