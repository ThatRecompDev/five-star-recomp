// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>The desktop resource and shape of the view at a descriptor slot (<see cref="ViewLiveness"/>); resource 0 for a null view.</summary>
internal readonly record struct SlotView(nint Resource, ViewShape Shape);
