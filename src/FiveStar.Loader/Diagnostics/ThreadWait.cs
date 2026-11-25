// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Diagnostics;

/// <summary>A thread's wait in progress (<see cref="WaitObjects"/>): the handle, the timeout, the start in seconds, the game frames that called the wait.</summary>
internal readonly record struct ThreadWait(nint Handle, uint Milliseconds, double Since, nint[] Frames);
