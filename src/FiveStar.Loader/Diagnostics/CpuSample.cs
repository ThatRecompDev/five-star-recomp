// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// One <c>--cpu-sample</c> sample of a thread, kept as addresses and named only when a report is written.
/// <paramref name="Pc"/>: the innermost frame for <see cref="CpuBucket.System"/> and <see cref="CpuBucket.Blocked"/>
/// (the wait stub), or a DLL our code called for <see cref="CpuBucket.Ours"/>; <paramref name="Function"/>: the start of
/// the innermost game function on the stack, when no frame of ours is inside it; <paramref name="Site"/>: the game's
/// return address into our code, when the thread is in it; <paramref name="Wait"/>: the loader wait it is in
/// (<see cref="BlockingWaits"/>); <paramref name="Ours"/>: a frame of our code comes before any game frame.
/// </summary>
internal readonly record struct CpuSample(CpuBucket Bucket, ulong Pc, ulong Function, ulong Site, string? Wait, bool Ours = false);
