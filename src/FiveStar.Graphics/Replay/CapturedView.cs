// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Graphics.Replay;

/// <summary>
/// One descriptor of the bindless heap: at <see cref="Index"/>, a view of <see cref="Resource"/> (null for a null
/// view) with <see cref="Description"/>, the D3D12_SHADER_RESOURCE_VIEW_DESC or D3D12_UNORDERED_ACCESS_VIEW_DESC bytes
/// in hexadecimal (<see cref="DescriptionBytes"/> of them; empty for a view of the whole resource, written without a desc).
/// </summary>
public sealed record CapturedView(uint Index, CapturedViewKind Kind, string? Resource, string Description)
{
    /// <summary>Bytes of both view descs (d3d12.h).</summary>
    public const int DescriptionBytes = 40;
}
