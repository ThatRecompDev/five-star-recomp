// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// What <see cref="TraceRecords"/> looks up: the address-map state of a console address, game memory at an address
/// (0 when unreadable), and the game's own heap record behind a window offset (0 without the game's heap).
/// </summary>
internal sealed record TraceRecordSources(Func<ulong, string> MapState, Func<ulong, int, nint> Memory, Func<uint, nint> GameRecord);
