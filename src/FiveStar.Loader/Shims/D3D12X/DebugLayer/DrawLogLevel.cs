// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary><c>--log-draws</c>: how much of the draw trace's per-draw text reaches the log.</summary>
internal enum DrawLogLevel
{
    /// <summary>No per-draw text; traced and watched work still reads back and its readbacks are logged.</summary>
    Off,

    /// <summary>Only traced work (<c>--d3d12-trace-pipeline</c> matches) is written; the window draws are neither written nor read back.</summary>
    Brief,

    /// <summary>Also the first draws and the draws after each captured present, each with a readback of its first target.</summary>
    Full,
}
