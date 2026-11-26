// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Graphics;
using FiveStar.Graphics.Rendering;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using FiveStar.Loader.Tests.Support;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// A probe is read only after the game submitted the list it was copied on: boot w116d read the exposure textures as all
/// zero where the report came before the game submitted the list.
/// </summary>
public sealed class DrawProbeSubmissionTests
{
    [WarpFact]
    public void A_probe_waits_for_its_list_to_be_submitted()
    {
        using var gpu = GpuDevice.CreateWarp(debugLayer: false);
        var device = gpu.Device;
        using var context = new CommandContext(device);
        using var heap = device.CreateDescriptorHeap(new Vortice.Direct3D12.DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 1));
        using var target = device.CreateCommittedResource(HeapType.Default, Vortice.Direct3D12.ResourceDescription.Texture2D(Format.R32G32B32A32_Float, 1, 1, 1, 1, flags: ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget);
        device.CreateRenderTargetView(target, null, heap.GetCPUDescriptorHandleForHeapStart());

        var text = new StringWriter();
        var probes = new DrawProbes(new LoaderLog(text, null, false));
        nint key = 0x1234;

        context.List.ClearRenderTargetView(heap.GetCPUDescriptorHandleForHeapStart(), new Vortice.Mathematics.Color4(0.5f, -1, -1, 0));
        probes.Record(device.NativePointer, context.List.NativePointer, target.NativePointer, (int)ResourceStates.RenderTarget, "exposure", key, 0);

        Assert.True(probes.Report(_ => 0));
        Assert.DoesNotContain("exposure", text.ToString(), StringComparison.Ordinal);

        context.ExecuteAndWait();
        probes.Submitted(key);

        Assert.False(probes.Report(_ => 0));
        Assert.Contains("float mean (0.5, -1, -1, 0)", text.ToString(), StringComparison.Ordinal);
    }
}
