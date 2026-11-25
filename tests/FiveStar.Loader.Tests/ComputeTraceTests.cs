// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Shims.D3D12X;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Shims.D3D12X.Present;
using FiveStar.Loader.Tests.Support;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// <c>--d3d12-trace-pipeline</c> covers compute work: a Dispatch whose compute pipeline's programs match is logged with
/// its group counts, its pipeline by program name (it has no rectangle-list description) and the compute user SGPRs,
/// and the list's submission names the queue it ran on. Before, only draws and indirect executions were traced, and a
/// compute pipeline showed as "unknown".
/// </summary>
public sealed unsafe class ComputeTraceTests
{
    private const string Program = "sgd1_0d3be303_t32_scattering_transmittance_system~CS_GeneratePrecisionVolumeDistanceField_94f33aef_Wrapped";
    private const nint Pipeline = 0x7A11_0000;
    private const uint TableOffset = 0x5c0;
    private const int QueueNumber = 3;

    [WarpFact]
    public void A_traced_dispatch_logs_its_compute_pipeline_groups_sgprs_and_queue()
    {
        using var log = new StringWriter();
        using var test = TestRuntime.Create(log: log);
        var device = new XboxDevice(test);
        var d3d12 = test.Runtime.D3D12X;
        var loaderLog = new LoaderLog(log, null, false);
        using var capture = new FrameCapture(null, [1], loaderLog);

        d3d12.EnableCapture(capture, traceDraws: true, "GeneratePrecisionVolumeDistanceField");
        QueueProgress.Name(Pipeline, Program);
        var trace = d3d12.DrawTrace!;
        trace.ReportDue(1);

        var list = (XboxObject*)device.ComputeList();
        d3d12.Rectangles.SetPipeline(list, Pipeline);
        CommandListBindings.Of(list)->Compute[UserData.ComputeBase] = TableOffset;

        trace.Dispatch(list, 72, 18, 9);
        nint submitted = (nint)list;
        trace.Submitted(QueueNumber, &submitted, 1);

        string text = log.ToString();
        Assert.Contains($"[draws] dispatch: list 0x{(nint)list:x}, groups 72 x 18 x 9", text, StringComparison.Ordinal);
        Assert.Contains($"pipeline 0x{Pipeline:x}: {Program}", text, StringComparison.Ordinal);
        Assert.Contains($"compute user sgprs: 0 0 0 0 {TableOffset:x}", text, StringComparison.Ordinal);
        Assert.Contains($"list 0x{(nint)list:x} with traced work submitted on queue #{QueueNumber}", text, StringComparison.Ordinal);

        // A pipeline the filter does not name is not traced.
        QueueProgress.Name(Pipeline + 1, "sgd1_0d40639f_t32_scattering_transmittance_system~CS_ClassifyRayTiles_b9fc98e5_Wrapped");
        d3d12.Rectangles.SetPipeline(list, Pipeline + 1);
        trace.Dispatch(list, 1, 1, 1);
        Assert.DoesNotContain("groups 1 x 1 x 1", log.ToString(), StringComparison.Ordinal);
    }
}
