// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// One capture under way: its plan and readback buffer, the folder it goes to, and the list it was recorded on with
/// that list's submission value and reset count then (<see cref="DispatchCaptures"/> writes it once that list's next
/// submission finished, and releases the buffer once the list was reset since, as <see cref="NanScan"/> does).
/// </summary>
internal sealed record DispatchCaptureJob(nint List, CapturePlan Plan, ID3D12Resource Readback, string Folder, ulong Submitted, long Resets, long At);
