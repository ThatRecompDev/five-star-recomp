// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>One buffer copy of a captured range: <see cref="Bytes"/> of desktop buffer <see cref="Buffer"/> from <see cref="SourceOffset"/>, to <see cref="TargetOffset"/> of the range.</summary>
internal readonly record struct CapturePiece(nint Buffer, ulong SourceOffset, ulong TargetOffset, ulong Bytes);
