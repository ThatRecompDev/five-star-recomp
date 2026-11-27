// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// Turns the driver's background shader work off for the device (ID3D12Device6::SetBackgroundProcessingMode,
/// D3D12_BACKGROUND_PROCESSING_MODE_DISABLE_BACKGROUND_WORK). NVIDIA's driver recompiles the shaders in use on the
/// "D3D Background Thread"s and keeps the results ("optional shaders") in the same 512 MiB tables as the first
/// compiles; in boot w222 they were 169 of the 524 MiB at which the tables could not grow and the device was removed,
/// and all of the growth of its last two minutes. Windows may refuse the mode outside developer mode: logged then.
/// <c>--driver-background-work</c> leaves the driver's default.
/// </summary>
internal static unsafe class BackgroundProcessing
{
    /// <summary>ID3D12Device6::SetBackgroundProcessingMode, D3D12_BACKGROUND_PROCESSING_MODE_DISABLE_BACKGROUND_WORK and D3D12_MEASUREMENTS_ACTION_KEEP_ALL (d3d12.h).</summary>
    private const int SetBackgroundProcessingModeSlot = 65;
    private const int DisableBackgroundWork = 2;
    private const int KeepAll = 0;

    private static readonly Guid Device6Iid = new("c70b221b-40e4-4a17-89af-025a0727a6dc");

    /// <summary>Whether the driver keeps its default (<c>--driver-background-work</c>).</summary>
    public static bool Keep { get; set; }

    /// <summary>Sets the mode on <paramref name="device"/>, once it is made.</summary>
    public static void Apply(nint device, LoaderLog log)
    {
        if (Keep || device == 0)
        {
            return;
        }

        nint device6 = 0;
        Guid iid = Device6Iid;
        int hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)ComObject.Slot(device, ComObject.QueryInterfaceSlot))(device, &iid, &device6);

        if (HResult.Failed(hr) || device6 == 0)
        {
            log.Info($"  [d3d12_xs] driver background work stays on: no ID3D12Device6 ({HResult.Format(hr)})");

            return;
        }

        hr = ((delegate* unmanaged<nint, int, int, nint, int*, int>)ComObject.Slot(device6, SetBackgroundProcessingModeSlot))(device6, DisableBackgroundWork, KeepAll, 0, null);
        ComObject.Release(device6);
        log.Info($"  [d3d12_xs] driver background shader work off -> {HResult.Format(hr)}");
    }
}
