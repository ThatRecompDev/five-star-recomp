// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// The copies between a depth twin's sides recorded inside a game list (<see cref="DepthTwinSides"/>), counted by
/// direction; the first ones and every <see cref="LogEvery"/>-th are logged, as the preludes are (<see cref="DepthTwinPrelude"/>).
/// </summary>
internal static class DepthTwinLog
{
    private const long LoggedFirst = 32;
    private const long LogEvery = 1000;

    private static long _toPlane;
    private static long _toTwin;

    public static long ToPlane => Interlocked.Read(ref _toPlane);

    public static long ToTwin => Interlocked.Read(ref _toTwin);

    /// <summary>An in-list copy of <paramref name="twin"/> into its plane (<paramref name="toPlane"/>) or of the plane into the twin was recorded.</summary>
    public static void InList(bool toPlane, DepthTwin twin)
    {
        long count = Count(toPlane);

        if (count > LoggedFirst && count % LogEvery != 0)
        {
            return;
        }

        string direction = toPlane ? "twin->plane" : "plane->twin";
        ShimContext.Log?.Info($"  [d3d12_xs] depth twins: in-list {direction} (twin 0x{twin.Twin:x} format {twin.Format} of depth 0x{twin.Depth:x}, {twin.Width}x{twin.Height}); so far {ToTwin} plane->twin, {ToPlane} twin->plane in lists");
    }

    private static long Count(bool toPlane)
    {
        if (toPlane)
        {
            return Interlocked.Increment(ref _toPlane);
        }

        return Interlocked.Increment(ref _toTwin);
    }
}
