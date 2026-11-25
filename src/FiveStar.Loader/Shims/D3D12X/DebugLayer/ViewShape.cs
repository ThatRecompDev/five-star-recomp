// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>What the shim wrote at a descriptor slot (<see cref="ViewLiveness"/>).</summary>
internal enum ViewShape : byte
{
    Null,
    BufferSrv,
    TextureSrv,
    BufferUav,
    TextureUav,
}
