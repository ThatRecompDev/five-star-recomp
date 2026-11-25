// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>What <see cref="ViewLiveness"/> found wrong with a table record's view.</summary>
internal enum ViewFinding
{
    None,
    Released,
    UavInReadOnlyRange,
    ReadOnlyInUavRange,
    TextureBehindBufferRecord,
}
