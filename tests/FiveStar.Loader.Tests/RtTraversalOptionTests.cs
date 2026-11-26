// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Host;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// <c>--rt-traversal</c> lets the game's traversal programs run: it takes <c>--raytracing</c>'s paths, which write the
/// structures they walk, and keeps the stand-ins of passes still held; <c>--raytracing</c> alone does not set it.
/// </summary>
public sealed class RtTraversalOptionTests
{
    [Fact]
    public void Rt_traversal_takes_the_ray_tracing_paths()
    {
        var options = new CommandLineParser(["--rt-traversal"]).Parse();

        Assert.True(options.RtTraversal);
        Assert.True(options.RayTracing);
        Assert.False(options.NoRtStandIns);
    }

    [Fact]
    public void Ray_tracing_alone_is_not_rt_traversal()
    {
        var options = new CommandLineParser(["--raytracing"]).Parse();

        Assert.True(options.RayTracing);
        Assert.False(options.RtTraversal);
    }
}
