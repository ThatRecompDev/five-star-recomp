// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Diagnostics;

/// <summary>One operation on a wait object: what happened ("signaled", "reset", "wait", "woke", ..), by which thread, when, and the game's calling address (0: none found).</summary>
internal readonly record struct WaitOperation(string Verb, uint Thread, double At, nint Caller);
