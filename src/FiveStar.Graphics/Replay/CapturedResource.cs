// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Graphics.Replay;

/// <summary>
/// One buffer or texture of a capture, by its D3D12_RESOURCE_DESC fields (dimension 1 is a buffer). Its bytes before the
/// dispatch are zeros, then <see cref="Input"/> (a blob of <see cref="Bytes"/>), then <see cref="Fills"/>, then
/// <see cref="Patches"/>; <see cref="Output"/> is what it held after the dispatch, the reference a replay is compared
/// with (null for a resource only read). A texture's blobs are laid out by <see cref="Footprints"/>, one per subresource.
/// <see cref="Id"/> is the name views use, <see cref="Label"/> what it was in the game (a console address range, a
/// texture view, the descriptor window).
/// </summary>
public sealed record CapturedResource(
    string Id,
    string Label,
    int Dimension,
    ulong Width,
    uint Height,
    ushort DepthOrArraySize,
    ushort MipLevels,
    int Format,
    int Flags,
    ulong Bytes,
    string? Input,
    string? Output)
{
    /// <summary>D3D12_RESOURCE_DIMENSION_BUFFER.</summary>
    public const int BufferDimension = 1;

    public IReadOnlyList<CapturedFootprint>? Footprints { get; init; }

    public IReadOnlyList<CapturedFill>? Fills { get; init; }

    public IReadOnlyList<CapturedPatch>? Patches { get; init; }

    /// <summary>The console GPU address of byte 0, for a range of game memory.</summary>
    public ulong? ConsoleAddress { get; init; }

    public bool IsBuffer => Dimension == BufferDimension;
}
