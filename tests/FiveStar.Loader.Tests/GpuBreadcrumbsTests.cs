// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Graphics;
using FiveStar.Graphics.Rendering;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Shims.D3D12X;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using FiveStar.Loader.Tests.Support;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// --d3d12-breadcrumbs keeps every operation of a long list (w79's lists stopped at operations 520 to 3229, named "?"
/// past the 512 kept), each with its pipeline, counts and root tables.
/// </summary>
public sealed unsafe class GpuBreadcrumbsTests
{
    private const int Operations = 3300;
    private const nint Pipeline = 0x7000;

    [WarpFact]
    public void An_operation_past_the_first_512_is_named_with_its_pipeline_and_counts()
    {
        using var gpu = GpuDevice.CreateWarp(debugLayer: false);
        using var context = new CommandContext(gpu.Device);
        var wrapper = (XboxObject*)NativeMemory.AllocZeroed(XboxInterfaces.CommandListObjectSize);

        try
        {
            GpuBreadcrumbs.Enable(true);
            GpuBreadcrumbs.Attach(gpu.Device.NativePointer, new LoaderLog(new StringWriter(), null, false));
            wrapper->Inner = context.List.NativePointer;
            uint serial = GpuBreadcrumbs.Begin(wrapper);
            ListActivity.Of(wrapper)->Serial = serial;
            ListActivity.Of(wrapper)->Pipeline = Pipeline;

            for (uint i = 0; i < Operations; i++)
            {
                GpuBreadcrumbs.Before(wrapper, MarkedKind.Draw, i, 1, 0);
            }

            context.ExecuteAndWait();

            string? last = GpuBreadcrumbs.OperationText(serial, Operations - 1, Name);
            Assert.Equal($"draw ({Operations - 1} x 1), pipeline PS_Test", last);
        }
        finally
        {
            GpuBreadcrumbs.Detach();
            NativeMemory.Free(wrapper);
        }
    }

    [Fact]
    public void An_operation_names_the_window_offsets_of_its_root_tables()
    {
        var operation = new MarkedOperation { Kind = MarkedKind.Dispatch, Compute = true, TableCount = 2 };
        operation.Tables[0] = 0x40;
        operation.Tables[1] = 0x1A0;

        Assert.Equal("; compute tables at window 0x40, 0x1a0", GpuBreadcrumbs.Tables(&operation));
    }

    private static string Name(nint pipeline)
    {
        if (pipeline == Pipeline)
        {
            return "PS_Test";
        }

        return "?";
    }
}
