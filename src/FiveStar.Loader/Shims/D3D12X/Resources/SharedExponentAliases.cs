// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

// Ported from victory-fork (github.com/fxceless/victory-fork, commit 578d459: crates/victory-gpu/src/resource.rs
// `alias_of`, texture.rs `alias`, draw.rs), itself built on VIctory. Copyright (c) the VIctory and victory-fork
// contributors; GPL-3.0-or-later.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Shims.D3D12X.States;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// The game packs light as R9G9B9E5 into R32_UINT textures with compute programs (which cannot write R9G9B9E5) and
/// reads it through R9G9B9E5 views placed at those textures' addresses (sky, sky and light probes, bounced light). On
/// the console that is one memory; here each such view reads an R9G9B9E5 shadow of the R32_UINT texture, made at the
/// first view, and the texels are copied into the shadow before a draw or dispatch reads it: at a list's first read and
/// again after any barrier the list put on the source. The hardware then loads, samples, gathers and filters the light
/// at every level, layer, cube face and slice (no program decodes it). On by default; <c>--no-shared-exponent-copies</c>
/// keeps the class-3 decode in the programs (<see cref="Binding.ImageViewClasses"/>).
/// </summary>
internal static unsafe class SharedExponentAliases
{
    /// <summary>DXGI_FORMAT_R32_UINT and R9G9B9E5_SHAREDEXP.</summary>
    private const int R32Uint = 42;
    private const int SharedExponent = 67;

    /// <summary>The most levels times layers copied (a cube of nine levels), as the fork.</summary>
    private const int MaxParts = 54;

    /// <summary>Records of one table searched for a shadow's view at a draw (a program that indexes its T#s reaches 32768).</summary>
    private const uint ScanLimit = 4096;

    private const int BitsPerWord = 32;
    private const int GrowthFactor = 2;

    private static readonly ConcurrentDictionary<nint, SharedExponentAlias?> BySource = new();
    private static readonly ConcurrentDictionary<uint, SharedExponentAlias> ByIndex = new();
    private static readonly object Gate = new();
    private static uint[] _indices = [];
    private static volatile bool _any;

    public static bool Enabled { get; set; } = true;

    /// <summary>Whether any shadow exists (the draw and barrier hooks do nothing before).</summary>
    public static bool Any => _any;

    /// <summary>Whether a view in <paramref name="viewFormat"/> of a texture read in <paramref name="readFormat"/> goes to a shadow.</summary>
    public static bool Takes(int viewFormat, int readFormat)
    {
        return Enabled && viewFormat == SharedExponent && readFormat == R32Uint;
    }

    /// <summary>The shadow of the GPU-written R32_UINT <paramref name="source"/>, made on first use; null when it cannot be.</summary>
    public static SharedExponentAlias? For(nint device, GpuTexture source)
    {
        if (!source.GpuWritten || source.DesktopFormat != R32Uint)
        {
            return null;
        }

        if (BySource.TryGetValue(source.Desktop, out var made) && (made is null || made.Source == source))
        {
            return made;
        }

        lock (Gate)
        {
            if (BySource.TryGetValue(source.Desktop, out made) && (made is null || made.Source == source))
            {
                return made;
            }

            made = Make(device, source);
            BySource[source.Desktop] = made;
            _any |= made is not null;

            return made;
        }
    }

    /// <summary>The view at game descriptor heap index <paramref name="index"/> reads <paramref name="alias"/>'s shadow.</summary>
    public static void NoteView(uint index, SharedExponentAlias alias)
    {
        ByIndex[index] = alias;

        lock (Gate)
        {
            int word = (int)(index / BitsPerWord);

            if (word >= _indices.Length)
            {
                var grown = new uint[Math.Max(word + 1, _indices.Length * GrowthFactor)];
                _indices.CopyTo(grown, 0);
                Volatile.Write(ref _indices, grown);
            }

            _indices[word] |= 1u << (int)(index % BitsPerWord);
        }
    }

    /// <summary>A barrier of <paramref name="list"/> named the desktop <paramref name="resource"/>: if it is a source, its next read copies.</summary>
    [HotPath]
    public static void Used(nint list, nint resource)
    {
        if (BySource.TryGetValue(resource, out var alias) && alias is not null)
        {
            alias.Used(list);
        }
    }

    /// <summary><paramref name="list"/> closed: its copies count for nothing in its next recording.</summary>
    public static void Closed(nint list)
    {
        foreach (var alias in BySource.Values)
        {
            alias?.Used(list);
        }
    }

    /// <summary>
    /// Before a draw or dispatch on <paramref name="list"/>: each shadow whose view the tables the pipeline reaches hold
    /// (<paramref name="userData"/> pointers into <paramref name="window"/>) gets the source's texels unless the list
    /// copied them since it last used the source.
    /// </summary>
    [HotPath]
    public static void BeforeWork(nint list, ListResourceStates states, uint* userData, byte* window, TableReachEntry[] tables)
    {
        var indices = Volatile.Read(ref _indices);

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
            uint records = entry.Records;

            if (records > ScanLimit)
            {
                records = ScanLimit;
            }

            // 64-bit: a table offset near 4 GiB must not wrap past the check (as in DepthTwins' writer scan).
            for (uint i = 0; i < records && (ulong)table + ((i + 1UL) * DescriptorWindow.ViewRecordBytes) <= DescriptorWindow.RecordsEnd; i++)
            {
                uint? index = DebugLayer.DepthUavs.HeapIndex((uint*)(window + table + (i * DescriptorWindow.ViewRecordBytes)));

                if (index is uint at && at / BitsPerWord < indices.Length && (indices[at / BitsPerWord] & (1u << (int)(at % BitsPerWord))) != 0)
                {
                    Read(list, states, at);
                }
            }
        }
    }

    private static void Read(nint list, ListResourceStates states, uint index)
    {
        if (!ByIndex.TryGetValue(index, out var alias) || alias.CurrentOn(list))
        {
            return;
        }

        var source = alias.Source;
        int sourceState = states.Current(source.Desktop, source.State);
        int shadowState = states.Current(alias.Shadow, SharedExponentCopy.Common);
        int after = SharedExponentCopy.Record(list, alias, sourceState, shadowState, states.LegalStates);

        if (after < 0)
        {
            return;
        }

        states.Set(alias.Shadow, SharedExponentCopy.Common, after);
        alias.Copied(list);
    }

    private static SharedExponentAlias? Make(nint device, GpuTexture source)
    {
        Marshal.AddRef(device);
        using var desktop = new ID3D12Device(device);
        Marshal.AddRef(source.Desktop);
        using var texture = new ID3D12Resource(source.Desktop);
        var description = texture.Description;

        int layers = description.Dimension == ResourceDimension.Texture3D ? 1 : description.DepthOrArraySize;
        int parts = description.MipLevels * layers;

        if (description.SampleDescription.Count > 1 || parts == 0 || parts > MaxParts || description.Dimension == ResourceDimension.Buffer)
        {
            return null;
        }

        var shadowDescription = description;
        shadowDescription.Format = Format.R9G9B9E5_SharedExp;
        shadowDescription.Flags = ResourceFlags.None;
        shadowDescription.Layout = TextureLayout.Unknown;
        shadowDescription.Alignment = 0;

        var written = Footprints(desktop, description, parts, out ulong bytes);
        var read = Footprints(desktop, shadowDescription, parts, out ulong readBytes);

        if (bytes == 0 || bytes != readBytes || !Alike(written, read))
        {
            return null;
        }

        var shadow = desktop.CreateCommittedResource(HeapType.Default, shadowDescription, Vortice.Direct3D12.ResourceStates.Common);
        var buffer = desktop.CreateCommittedResource(HeapType.Default, Vortice.Direct3D12.ResourceDescription.Buffer(bytes), Vortice.Direct3D12.ResourceStates.Common);
        nint shadowPointer = shadow.NativePointer;
        nint bufferPointer = buffer.NativePointer;
        Marshal.AddRef(shadowPointer);
        Marshal.AddRef(bufferPointer);

        shadow.Dispose();
        buffer.Dispose();

        ShimContext.Log?.Info($"[d3d12_xs] R9G9B9E5 views of the R32_UINT texture at 0x{source.Address:x} ({description.Width}x{description.Height}, {parts} parts) read a shadow the texels are copied into");

        return new SharedExponentAlias(source, shadowPointer, bufferPointer, written, read);
    }

    private static CopyFootprint[] Footprints(ID3D12Device device, Vortice.Direct3D12.ResourceDescription description, int parts, out ulong bytes)
    {
        var footprints = new PlacedSubresourceFootPrint[parts];
        var rows = new uint[parts];
        var rowBytes = new ulong[parts];
        device.GetCopyableFootprints(description, 0, (uint)parts, 0, footprints, rows, rowBytes, out bytes);

        return [.. footprints.Select(part => new CopyFootprint(part.Offset, (int)part.Footprint.Format, part.Footprint.Width, part.Footprint.Height, part.Footprint.Depth, part.Footprint.RowPitch))];
    }

    private static bool Alike(CopyFootprint[] written, CopyFootprint[] read)
    {
        for (int part = 0; part < written.Length; part++)
        {
            if (written[part] with { Format = 0 } != read[part] with { Format = 0 })
            {
                return false;
            }
        }

        return true;
    }
}
