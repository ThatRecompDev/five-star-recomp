// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Graphics.Replay;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// A resource <see cref="DispatchCaptures"/> copies from the game's list, before the dispatch and (for an
/// <see cref="Output"/>) after it: a buffer range as <see cref="Pieces"/> of desktop buffers, or a desktop texture
/// (<see cref="Texture"/>, every subresource by <see cref="CapturedResource.Footprints"/>, from its state on the list).
/// </summary>
internal sealed class CaptureSource
{
    public CaptureSource(CapturedResource shape, IReadOnlyList<CapturePiece> pieces, nint texture, int home)
    {
        Shape = shape;
        Pieces = pieces;
        Texture = texture;
        Home = home;
    }

    public CapturedResource Shape { get; }

    public IReadOnlyList<CapturePiece> Pieces { get; }

    /// <summary>The desktop texture; 0 for a buffer range.</summary>
    public nint Texture { get; }

    /// <summary>The texture's home state (D3D12_RESOURCE_STATES), its state on a list that set none.</summary>
    public int Home { get; }

    /// <summary>Whether it is read back after the dispatch too (a program can write it).</summary>
    public bool Output { get; set; }

    /// <summary>Where its bytes before the dispatch go in the readback buffer.</summary>
    public ulong Before { get; set; }

    /// <summary>Where its bytes after the dispatch go.</summary>
    public ulong After { get; set; }
}
