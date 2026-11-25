// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Diagnostics;

/// <summary>A thread's wait the shims saw: on <see cref="Handle"/>, or on <see cref="Several"/> (the first is <see cref="Handle"/>).</summary>
internal readonly record struct ThreadWait(nint Handle, uint Milliseconds, double Since, nint[] Frames)
{
    /// <summary>Every object of a wait on several; null for a wait on one.</summary>
    public nint[]? Several { get; init; }

    /// <summary>Whether a wait on several waits for all of them, not any.</summary>
    public bool All { get; init; }
}
