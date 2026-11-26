// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Diagnostics;

/// <summary>Where a <c>--cpu-sample</c> sample found a thread.</summary>
internal enum CpuBucket
{
    /// <summary>Running in a mapped image's own code (the game's exe, or a DLL the loader mapped).</summary>
    Game,

    /// <summary>Running in the loader's code (a shim, JIT code) or in a DLL our code called.</summary>
    Ours,

    /// <summary>Running in an OS-loaded DLL the game called itself.</summary>
    System,

    /// <summary>Its innermost frame is a waiting system call.</summary>
    Blocked,

    /// <summary>Its snapshot could not be taken.</summary>
    Unread,
}
