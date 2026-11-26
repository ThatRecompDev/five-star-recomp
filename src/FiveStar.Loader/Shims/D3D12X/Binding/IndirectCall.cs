// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// One ExecuteIndirect: the game's signature wrapper, its argument kind and stride (<see cref="CommandSignatures.Clampable"/>,
/// looked up once per call), the most records it runs and the console address of its first argument (0: not known).
/// </summary>
internal readonly record struct IndirectCall(nint Signature, int Kind, uint Stride, uint MaxCount, ulong ArgumentsAt = 0);
