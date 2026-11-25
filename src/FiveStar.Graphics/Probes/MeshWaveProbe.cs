// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Graphics.Pipelines;
using FiveStar.Graphics.Rendering;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace FiveStar.Graphics.Probes;

/// <summary>
/// Measures the hardware wave a mesh shader runs in: D3D12 reports only the range of every stage
/// (WaveLaneCountMin-Max), and a mesh program whose floor lies inside that range needs its superstep form unless the
/// driver runs mesh shaders wide enough. An RX 7800 XT reports 32-64 and runs every mesh shader in 64-lane waves, at
/// 32, 64 and 128 threads and with long loops alike. The probe (mesh_wave_probe.hlsl) dispatches groups of 128 threads,
/// as the console's mesh programs have, and keeps the least and greatest WaveGetLaneCount.
/// </summary>
public static class MeshWaveProbe
{
    private const string Resource = "FiveStar.Graphics.Probes.mesh_wave_probe.dxil";
    private const int Groups = 16;
    private const int Bytes = 8;

    // The least lane count starts at its greatest value, the greatest at 0.
    private const uint NoLeast = uint.MaxValue;

    /// <summary>The narrowest wave the probe's groups ran in, or null when the probe could not run.</summary>
    public static int? Measure(ID3D12Device device)
    {
        try
        {
            return Run(device);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>The compiled probe, embedded in this assembly.</summary>
    public static byte[] Shader()
    {
        using var stream = typeof(MeshWaveProbe).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"{Resource} is not embedded");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);

        return bytes;
    }

    private static int? Run(ID3D12Device device)
    {
        RootParameter1[] parameters = [new(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All)];
        using var root = device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None, parameters));
        using var state = new PipelineBuilder(device, root).Mesh(new MeshProgram(Shader(), null, [], Format.Unknown));
        using var arena = new ResourceArena(device);
        using var context = new CommandContext(device);
        using var list = context.List.QueryInterface<ID3D12GraphicsCommandList6>();

        var start = arena.UploadBuffer(BitConverter.GetBytes((ulong)NoLeast));
        var lanes = arena.Own(device.CreateCommittedResource(
            new HeapProperties(HeapType.Default), HeapFlags.None, ResourceDescription.Buffer(Bytes, ResourceFlags.AllowUnorderedAccess), ResourceStates.CopyDest));
        var readback = arena.ReadbackBuffer(Bytes);

        list.CopyBufferRegion(lanes, 0, start, 0, Bytes);
        list.ResourceBarrierTransition(lanes, ResourceStates.CopyDest, ResourceStates.UnorderedAccess);
        list.SetGraphicsRootSignature(root);
        list.SetGraphicsRootUnorderedAccessView(0, lanes.GPUVirtualAddress);
        list.SetPipelineState(state);

        list.DispatchMesh(Groups, 1, 1);
        list.ResourceBarrierTransition(lanes, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
        list.CopyBufferRegion(readback, 0, lanes, 0, Bytes);
        context.ExecuteAndWait();

        var words = ResourceArena.Read(readback, Bytes);
        var least = BitConverter.ToInt32(words, 0);
        var greatest = BitConverter.ToInt32(words, sizeof(int));

        if (least <= 0 || greatest < least)
        {
            return null;
        }

        return least;
    }
}
