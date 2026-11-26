// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>A D3D12_SHADER_RESOURCE_VIEW_DESC or D3D12_UNORDERED_ACCESS_VIEW_DESC as written: ten dwords, 40 bytes.</summary>
[InlineArray(Dwords)]
internal struct TextureViewDescription
{
    public const int Dwords = 10;

    private int _dword;
}
