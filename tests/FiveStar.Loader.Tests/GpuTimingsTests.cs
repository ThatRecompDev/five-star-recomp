// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Graphics;
using FiveStar.Graphics.Rendering;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using FiveStar.Loader.Tests.Support;
using Vortice.Direct3D12;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// --gpu-timings on WARP: a list's passes (split at a render-target change, a draw/dispatch switch or another compute
/// pipeline) get a timestamp each, the list's Close resolves them, and once the submission's fence passed they are summed per queue and pass,
/// named by their first pipeline.
/// </summary>
public sealed class GpuTimingsTests
{
    private const nint List = 0x1000;
    private const nint Culling = 1;
    private const nint Lighting = 2;
    private const nint Tracing = 3;

    [Fact]
    public void A_pass_is_named_by_its_first_program_without_container_and_hash()
    {
        Assert.Equal("meshlet_culling~CS_BatchCullPreSkinnedMeshlets", GpuTimings.PassName("sgd1_0c3d9198_t3c_meshlet_culling~CS_BatchCullPreSkinnedMeshlets_b589f3cd_Wrapped"));
        Assert.Equal("PS_DeferredPedTexturedNC", GpuTimings.PassName("sgd1_06d9436f_t32_PS_DeferredPedTexturedNC_1778b45a_Wrapped + sgd1_0184d13e_t41_MS_VS_PedTransformPreSkinD_1778b45a_Wrapped"));
        Assert.Equal("none", GpuTimings.PassName("none"));
    }

    [WarpFact]
    public void Each_pass_of_a_list_is_timed_and_summed_by_its_first_pipeline()
    {
        using var gpu = GpuDevice.CreateWarp(debugLayer: false);

        try
        {
            Run(gpu.Device);
        }
        finally
        {
            GpuTimings.Detach();
            GpuTimings.Name = Shims.D3D12X.QueueProgress.PipelineName;
        }
    }

    [WarpFact]
    public void Timing_a_pass_allocates_nothing_on_the_game_thread()
    {
        using var gpu = GpuDevice.CreateWarp(debugLayer: false);

        try
        {
            GpuTimings.Enable(true);
            GpuTimings.Attach(gpu.Device.NativePointer, null);
            using var context = new CommandContext(gpu.Device);
            nint list = context.List.NativePointer;
            GpuTimings.Reset(List);
            Passes(list);

            // Called before every draw, dispatch and indirect execution: no allocation per call.
            GpuTimings.Reset(List);
            long before = GC.GetAllocatedBytesForCurrentThread();
            Passes(list);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            GpuTimings.BeforeClose(List, list);
            context.ExecuteAndWait();
        }
        finally
        {
            GpuTimings.Detach();
        }
    }

    /// <summary>Compute, clamp and draw passes in turn (the first run also warms the code up).</summary>
    private static void Passes(nint list)
    {
        for (int pass = 0; pass < 100; pass++)
        {
            GpuTimings.Work(List, list, compute: true, Tracing);
            GpuTimings.Work(List, list, compute: true, GpuTimings.IndirectClampPipeline);
            GpuTimings.Work(List, list, compute: false, Lighting);
        }
    }

    private static void Run(ID3D12Device device)
    {
        GpuTimings.Enable(true);
        GpuTimings.Attach(device.NativePointer, null);
        GpuTimings.Name = Name;

        using var context = new CommandContext(device);
        using var queue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
        using var fence = device.CreateFence(1);
        nint list = context.List.NativePointer;

        // Dispatches of one pipeline are one pass; another compute pipeline, new targets and a draw begin the next, and a
        // split at a memory wait ends the pass before it (the queue waits there, not in the tracing pass).
        GpuTimings.Work(List, list, compute: true, Culling);
        GpuTimings.Work(List, list, compute: true, Culling);
        GpuTimings.Work(List, list, compute: true, Tracing);
        GpuTimings.Split(List, list);
        GpuTimings.Work(List, list, compute: true, Tracing);

        // An indirect draw's argument clamp is a pass of its own before the draw's.
        GpuTimings.Targets(List);
        GpuTimings.Work(List, list, compute: true, GpuTimings.IndirectClampPipeline);
        GpuTimings.Work(List, list, compute: false, Lighting);
        GpuTimings.BeforeClose(List, list);
        context.ExecuteAndWait();

        GpuTimings.Executed(1, queue.NativePointer, fence.NativePointer, 1, [List]);
        GpuTimings.CollectNow();
        string report = GpuTimings.Report(2);

        Assert.Contains("queue #1 busy ", report, StringComparison.Ordinal);
        Assert.Contains("(meshlet_culling~CS_Cull ", report, StringComparison.Ordinal);
        Assert.Contains("sr_tracing~CS_Trace ", report, StringComparison.Ordinal);

        Assert.Contains("(memory wait) ", report, StringComparison.Ordinal);
        Assert.Contains("loader:indirect-clamp ", report, StringComparison.Ordinal);
        Assert.Contains("PS_Light ", report, StringComparison.Ordinal);
        Assert.DoesNotContain("VS_Light", report, StringComparison.Ordinal);

        Assert.Contains("0 read after their block was reused", report, StringComparison.Ordinal);
        Assert.Contains("0 with more than 255 passes", report, StringComparison.Ordinal);

        // 300 compute passes: the block holds 255, and its last pass, which takes the rest, says so.
        GpuTimings.Reset(List);
        nint[] alternating = [Culling, Tracing];

        for (int pass = 0; pass < 300; pass++)
        {
            GpuTimings.Work(List, list, compute: true, alternating[pass & 1]);
        }

        GpuTimings.BeforeClose(List, list);
        context.ExecuteAndWait();
        GpuTimings.Executed(1, queue.NativePointer, fence.NativePointer, 1, [List]);
        GpuTimings.CollectNow();
        report = GpuTimings.Report(3);

        Assert.Contains("meshlet_culling~CS_Cull +rest ", report, StringComparison.Ordinal);
        Assert.Contains("1 with more than 255 passes", report, StringComparison.Ordinal);
    }

    private static string Name(nint pipeline)
    {
        if (pipeline == Culling)
        {
            return "sgd1_0c3d9198_t3c_meshlet_culling~CS_Cull_b589f3cd_Wrapped";
        }

        if (pipeline == Tracing)
        {
            return "sgd1_0f2178a6_t32_sr_tracing~CS_Trace_0fd13247_Wrapped";
        }

        return "sgd2_1042914b_t32_PS_Light_a4ab6e63_Wrapped + sgd2_102f2093_t32_VS_Light_a4ab6e63_Wrapped";
    }
}
