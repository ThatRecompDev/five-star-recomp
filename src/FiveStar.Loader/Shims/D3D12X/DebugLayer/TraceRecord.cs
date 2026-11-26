// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// One entry of <c>--d3d12-trace-records</c>: a record's byte offset from a user SGPR pointer and, for
/// <c>offset@byte</c>, a byte offset inside the buffer its V# points to whose dword is shown too (a loop count a
/// program reads from a constant buffer past the first dwords). <c>offset@sgprN+byte</c> adds user SGPR
/// <see cref="DwordSgpr"/> to that byte offset (a per-draw material offset a program passes as a root constant), and a
/// trailing <c>&gt;sgprM</c> follows the dword as a record number in the window table user SGPR
/// <see cref="FollowSgpr"/> points to (a bindless texture index: the program loads the T# at that table + dword * 32).
/// </summary>
internal readonly record struct TraceRecord(uint Offset, uint? Dword = null, int? DwordSgpr = null, int? FollowSgpr = null);
