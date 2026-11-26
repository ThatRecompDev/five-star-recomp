// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Present;

/// <summary>How <see cref="GpuKeepAwake"/> runs.</summary>
internal enum GpuKeepAwakeMode
{
    /// <summary>Not at all (the default).</summary>
    Off,

    /// <summary>While the game submits (<c>--gpu-keep-awake</c>).</summary>
    On,

    /// <summary>On and off every 20 s, to compare both in one boot (<c>--gpu-keep-awake-turns</c>).</summary>
    Turns,
}
