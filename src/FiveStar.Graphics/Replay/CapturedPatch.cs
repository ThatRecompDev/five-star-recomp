// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Graphics.Replay;

/// <summary>The bytes of blob <see cref="Blob"/> written at <see cref="Offset"/> of a resource (the records a program reads).</summary>
public sealed record CapturedPatch(ulong Offset, string Blob);
