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
/// --gpu-timings on WARP: a list's passes (split at a render-target change or a draw/dispatch switch) get a timestamp
/// each, the list's Close resolves them, and once the submission's fence passed they are summed per queue and pass,
/// named by their first pipeline.
/// </summary>
public sealed class GpuTimingsTests
{
    private const nint List = 0x1000;
    private const nint Culling = 1;
    private const nint Lighting = 2;

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

    private static void Run(ID3D12Device device)
    {
        GpuTimings.Enable(true);
        GpuTimings.Attach(device.NativePointer, null);
        GpuTimings.Name = Name;

        using var context = new CommandContext(device);
        using var queue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
        using var fence = device.CreateFence(1);
        nint list = context.List.NativePointer;

        // Two dispatches are one pass; new targets and a draw begin the next.
        GpuTimings.Work(List, list, compute: true, Culling);
        GpuTimings.Work(List, list, compute: true, Lighting);
        GpuTimings.Targets(List);
        GpuTimings.Work(List, list, compute: false, Lighting);
        GpuTimings.BeforeClose(List, list);
        context.ExecuteAndWait();

        GpuTimings.Executed(1, queue.NativePointer, fence.NativePointer, 1, [List]);
        GpuTimings.CollectNow();
        string report = GpuTimings.Report(2);

        Assert.Contains("queue #1 busy ", report, StringComparison.Ordinal);
        Assert.Contains("(meshlet_culling~CS_Cull ", report, StringComparison.Ordinal);
        Assert.Contains("PS_Light ", report, StringComparison.Ordinal);
        Assert.DoesNotContain("VS_Light", report, StringComparison.Ordinal);
        Assert.Contains("0 read after their block was reused", report, StringComparison.Ordinal);
    }

    private static string Name(nint pipeline)
    {
        if (pipeline == Culling)
        {
            return "sgd1_0c3d9198_t3c_meshlet_culling~CS_Cull_b589f3cd_Wrapped";
        }

        return "sgd2_1042914b_t32_PS_Light_a4ab6e63_Wrapped + sgd2_102f2093_t32_VS_Light_a4ab6e63_Wrapped";
    }
}
