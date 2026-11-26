// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.Xg;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// A desktop texture standing for console memory at <see cref="Address"/>: <see cref="Layout"/> bytes in the console
/// tiling (docs/xg.md). The registry holds one reference on <see cref="Desktop"/>. A GPU-written texture (render
/// target, depth-stencil or UAV) is never filled from memory; any other is refreshed when the bytes changed.
/// </summary>
internal sealed class GpuTexture
{
    public GpuTexture(ulong address, ResourceDescription console, nint desktop, int desktopFormat, int state, bool gpuWritten, in XgSurface layout)
    {
        Address = address;
        Console = console;
        Desktop = desktop;

        DesktopFormat = desktopFormat;
        State = state;
        GpuWritten = gpuWritten;
        Layout = layout;
    }

    /// <summary>The console GPU address of the first byte.</summary>
    public ulong Address { get; }

    /// <summary>The resource desc the game gave (console layout and flags included).</summary>
    public ResourceDescription Console { get; }

    /// <summary>The desktop ID3D12Resource (one reference owned by the registry).</summary>
    public nint Desktop { get; }

    /// <summary>The DXGI format the desktop texture was created with.</summary>
    public int DesktopFormat { get; }

    /// <summary>The D3D12_RESOURCE_STATES the texture rests in between uploads.</summary>
    public int State { get; }

    public bool GpuWritten { get; }

    /// <summary>The console layout of the texture's bytes.</summary>
    public XgSurface Layout { get; }

    /// <summary>Bytes the texture spans in console memory.</summary>
    public ulong Size => Layout.Size;

    /// <summary>Whether the CPU maps the texture's memory, so it is checked for new bytes every frame (<see cref="GpuTextures.Watch"/>).</summary>
    public bool Watched { get; set; }

    /// <summary><see cref="UnmappedSince"/> of a texture whose memory is mapped.</summary>
    public const long Mapped = -1;

    /// <summary>The present at which memory under the texture was unmapped (<see cref="GpuTextureRegistry.Unmapped"/>); <see cref="Mapped"/> while it is mapped.</summary>
    public long UnmappedSince { get; set; } = Mapped;

    /// <summary>Whether memory under the texture is unmapped now.</summary>
    public bool OverUnmapped => UnmappedSince != Mapped;

    /// <summary>
    /// Whether a GPU copy (list slots 85, 86) wrote the desktop texture. Its contents are then on the desktop, not in
    /// the console memory (on the console the copy wrote that memory), so a refresh from memory would undo the copy.
    /// </summary>
    public bool CopiedInto { get; set; }

    /// <summary>The content hash of the bytes last uploaded; 0 before the first upload.</summary>
    public ulong UploadedHash { get; set; }

    /// <summary>Pages per word of the written-pages bitmap.</summary>
    private const int DirtyWordBits = 64;

    /// <summary>Held while the texture is refreshed from memory (one refresh at a time).</summary>
    public object RefreshGate { get; } = new();

    /// <summary>The bytes a page of <see cref="PageHashes"/> and <see cref="MarkPages"/> covers (an XMem page).</summary>
    public const ulong PageBytes = 0x10000;

    /// <summary>The content hash of each <see cref="PageBytes"/> page last uploaded; null until a fill recorded them.</summary>
    public ulong[]? PageHashes { get; set; }

    /// <summary>The pages written since the last refresh took them, one bit each; null for none.</summary>
    private ulong[]? _dirty;

    private readonly object _dirtyGate = new();

    /// <summary>The number of <see cref="PageBytes"/> pages the texture spans.</summary>
    public int Pages => (int)((Size + PageBytes - 1) / PageBytes);

    /// <summary>Marks the pages under bytes [<paramref name="start"/>, <paramref name="end"/>) of the texture as written.</summary>
    public void MarkPages(ulong start, ulong end)
    {
        if (end <= start || start >= Size)
        {
            return;
        }

        int first = (int)(start / PageBytes);
        int last = (int)((Math.Min(end, Size) - 1) / PageBytes);

        lock (_dirtyGate)
        {
            _dirty ??= new ulong[(Pages + DirtyWordBits - 1) / DirtyWordBits];

            for (int page = first; page <= last; page++)
            {
                _dirty[page / DirtyWordBits] |= 1UL << (page % DirtyWordBits);
            }
        }
    }

    /// <summary>The written pages' bits, cleared; null when none was written.</summary>
    public ulong[]? TakeDirty()
    {
        lock (_dirtyGate)
        {
            var dirty = _dirty;
            _dirty = null;

            return dirty;
        }
    }

    public bool Contains(ulong address)
    {
        return address - Address < Size;
    }

    /// <summary>Whether the game's desc describes this texture (same address, extent, format and mips; the layout field differs between slots).</summary>
    public bool Matches(ulong address, in ResourceDescription description)
    {
        return SameExtent(address, description) && description.Format == Console.Format;
    }

    /// <summary>Whether the game's desc has this texture's address, dimension, size and mips, whatever its format.</summary>
    public bool SameExtent(ulong address, in ResourceDescription description)
    {
        return address == Address && description.Dimension == Console.Dimension && description.Width == Console.Width
            && description.Height == Console.Height && description.DepthOrArraySize == Console.DepthOrArraySize
            && description.MipLevels == Console.MipLevels;
    }
}
