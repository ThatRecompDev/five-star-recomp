// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// One gathered indirect record (<see cref="IndirectGather"/>): the gather buffer and the slot it is clamped into, the
/// desktop buffer and offset it is read from, its argument kind and stride, and the segment of its list it runs in.
/// </summary>
internal readonly record struct GatherRecord(nint Target, nint Source, uint From, uint At, int Kind, uint Stride, int Segment);
