// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// The draw trace names the depth target a draw tests against: CreateDepthStencilView notes its views with the render
/// targets, and the trace used to look them up only among the shader views, so every DSV read as unseen.
/// </summary>
public sealed class DrawTraceDepthViewTests
{
    private const nint Handle = 0x1000;
    private const nint Resource = 0x2000;
    private const int DepthWrite = 0x10;

    [Fact]
    public void A_depth_view_noted_with_the_render_targets_names_its_resource()
    {
        var targets = new Dictionary<nint, (nint Resource, int Home)> { [Handle] = (Resource, DepthWrite) };

        string text = DrawTrace.DepthViewText(new TracedViews(), targets, Handle, _ => "2560x1440 format 19");

        Assert.Equal("resource 0x2000 2560x1440 format 19, home state 0x10", text);
    }

    [Fact]
    public void A_shader_view_at_the_handle_comes_first()
    {
        var views = new TracedViews();
        views.Write(Handle, "SRV of 0x3000");
        var targets = new Dictionary<nint, (nint Resource, int Home)> { [Handle] = (Resource, DepthWrite) };

        Assert.Equal("SRV of 0x3000", DrawTrace.DepthViewText(views, targets, Handle, _ => string.Empty));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x4000)]
    public void A_handle_with_no_view_is_unseen(long handle)
    {
        var targets = new Dictionary<nint, (nint Resource, int Home)> { [Handle] = (Resource, DepthWrite) };

        Assert.Equal("not a view this trace saw", DrawTrace.DepthViewText(new TracedViews(), targets, (nint)handle, _ => string.Empty));
    }
}
