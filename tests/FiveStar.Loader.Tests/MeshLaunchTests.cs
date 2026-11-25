// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Graphics;
using FiveStar.Graphics.Pipelines;
using FiveStar.Graphics.Rendering;
using FiveStar.Loader.Shims.D3D12X;
using FiveStar.Loader.Shims.D3D12X.Pipelines;
using FiveStar.Loader.Tests.Support;
using FiveStar.Shaders.Compilation;
using FiveStar.Shaders.Dxil;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;
using DescriptorHeapDescription = Vortice.Direct3D12.DescriptorHeapDescription;
using ResourceDescription = Vortice.Direct3D12.ResourceDescription;

namespace FiveStar.Loader.Tests;

/// <summary>
/// <c>--mesh-launch</c> on WARP: the amplification stage (dxc) in front of a mesh program the DXIL backend compiled with
/// the launch payload reads a record where it lies, bounds it as the clamp does (more than 65,535 groups run empty),
/// charges the list's budget (a record that no longer fits runs empty) and launches the mesh groups; a record the clamp
/// already bounded is launched as it is, uncharged. The mesh program counts its groups.
/// </summary>
public sealed class MeshLaunchTests
{
    private const int RecordBytes = 12;
    private const uint ListBudget = 1u << 27;
    private const uint GroupWork = 128;
    private const uint Left = 100;

    private const string Mesh = """
        struct LaunchPayload { uint unused; };
        struct Vertex { float4 position : SV_Position; };
        [numthreads(32, 1, 1)]
        [outputtopology("triangle")]
        void main(uint thread : SV_GroupIndex, out vertices Vertex outputs[1], out indices uint3 triangles[1], in payload LaunchPayload launch_payload)
        {
            SetMeshOutputCounts(0, 0);
            if (thread == 32u) { outputs[0].position = 0; }
            RWByteAddressBuffer groups = ResourceDescriptorHeap[0];
            if (thread == 0u) { groups.InterlockedAdd(0, 1u); }
        }
        """;

    [Fact]
    public void The_backend_declares_the_payload_and_only_such_mesh_programs_take_the_stage()
    {
        Assert.True(MeshLaunch.Accepts(DxilBackend.Compile(Mesh, "ms")));
        Assert.False(MeshLaunch.Accepts(DxilBackend.Compile(Mesh.Replace(", in payload LaunchPayload launch_payload", string.Empty, StringComparison.Ordinal), "ms")));
        Assert.False(MeshLaunch.Accepts([]));
    }

    [DxcFact]
    public void Records_are_bounded_charged_and_launched_by_the_stage()
    {
        using var gpu = GpuDevice.CreateWarp(debugLayer: false);
        var adapter = DesktopAdapter.Describe(gpu.Device.NativePointer);

        if (!adapter.MeshShaders)
        {
            return;
        }

        var compiler = new DxcLibraryCompiler(DxcOptions.Defaults(DxcLibraryCompiler.LibraryPath(PipelineFactAttribute.Dxc), retry: false));
        var (stage, messages) = compiler.CompileBytes(new CompileRequest(Path.Combine(Path.GetTempPath(), "mesh_launch.hlsl"), MeshLaunch.Source, "as", DisableOptimizations: false));
        Assert.True(stage is not null, messages);

        // (5, 1, 1) runs 5 groups and charges 640; (70000, 1, 1) is past 65,535 and runs empty; a clamped (3, 1, 1) runs 3.
        uint[] records = [5, 1, 1, 70000, 1, 1, 3, 1, 1, 2, 1, 1];
        var (groups, budget) = Run(gpu.Device, stage!, records, 0, [(MeshLaunch.ChargedRecord, 0), (MeshLaunch.ChargedRecord, 1), (MeshLaunch.ClampedRecord, 2)]);
        Assert.Equal(8u, groups);
        Assert.Equal(5 * GroupWork, budget);

        // With only 100 left in the budget, (2, 1, 1) asks for 256 and runs empty, and the budget is left as it was.
        (groups, budget) = Run(gpu.Device, stage!, records, ListBudget - Left, [(MeshLaunch.ChargedRecord, 3)]);
        Assert.Equal(0u, groups);
        Assert.Equal(ListBudget - Left, budget);
    }

    /// <summary>Launches each (mode, record index) and returns the mesh groups that ran and the budget after.</summary>
    private static (uint Groups, uint Budget) Run(ID3D12Device device, byte[] stage, uint[] records, uint budgetStart, (uint Mode, int Record)[] launches)
    {
        using var root = device.CreateRootSignature(BindlessRootSignature.Description(userConstants: false, meshLaunch: true));
        using var state = Pipeline(device, root, stage);
        using var heap = device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, DescriptorHeapFlags.ShaderVisible));
        using var arena = new ResourceArena(device);
        using var context = new CommandContext(device);
        using var list = context.List.QueryInterface<ID3D12GraphicsCommandList6>();

        var recordBuffer = arena.UploadBuffer(System.Runtime.InteropServices.MemoryMarshal.AsBytes(records.AsSpan()));
        var start = arena.UploadBuffer(BitConverter.GetBytes(budgetStart));
        var counter = arena.Own(Buffer(device, ResourceStates.UnorderedAccess));
        var budget = arena.Own(Buffer(device, ResourceStates.CopyDest));
        var readback = arena.ReadbackBuffer(2 * sizeof(uint));
        device.CreateUnorderedAccessView(counter, null, RawView(), heap.GetCPUDescriptorHandleForHeapStart());

        list.CopyBufferRegion(budget, 0, start, 0, sizeof(uint));
        list.ResourceBarrierTransition(budget, ResourceStates.CopyDest, ResourceStates.UnorderedAccess);
        list.SetDescriptorHeaps(new[] { heap });
        list.SetGraphicsRootSignature(root);
        list.SetPipelineState(state);

        foreach (var (mode, record) in launches)
        {
            MeshLaunch.Record(list.NativePointer, mode, recordBuffer.GPUVirtualAddress + (ulong)(record * RecordBytes), budget.GPUVirtualAddress);
        }

        list.ResourceBarrierTransition(counter, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
        list.ResourceBarrierTransition(budget, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
        list.CopyBufferRegion(readback, 0, counter, 0, sizeof(uint));
        list.CopyBufferRegion(readback, sizeof(uint), budget, 0, sizeof(uint));
        context.ExecuteAndWait();

        var words = ResourceArena.Read(readback, 2 * sizeof(uint));

        return (BitConverter.ToUInt32(words, 0), BitConverter.ToUInt32(words, sizeof(uint)));
    }

    private static ID3D12Resource Buffer(ID3D12Device device, ResourceStates initial)
    {
        return device.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None, ResourceDescription.Buffer(sizeof(uint), ResourceFlags.AllowUnorderedAccess), initial);
    }

    private static ID3D12PipelineState Pipeline(ID3D12Device device, ID3D12RootSignature root, byte[] stage)
    {
        using var stream = new PipelineSubobjects();
        stream.AddPointer(PipelineStateSubObjectType.RootSignature, root.NativePointer);
        stream.AddShader(PipelineStateSubObjectType.AmplificationShader, stage);
        stream.AddShader(PipelineStateSubObjectType.MeshShader, DxilBackend.Compile(Mesh, "ms"));

        stream.Add(PipelineStateSubObjectType.Rasterizer, RasterizerDescription.CullNone);
        stream.Add(PipelineStateSubObjectType.DepthStencil, DepthStencilDescription.None);
        stream.AddWords(PipelineStateSubObjectType.RenderTargetFormats, new int[9]);
        stream.AddWords(PipelineStateSubObjectType.DepthStencilFormat, [(int)Format.Unknown]);
        stream.Add(PipelineStateSubObjectType.SampleDescription, new SampleDescription(1, 0));

        return stream.Create(device);
    }

    private static UnorderedAccessViewDescription RawView()
    {
        return new UnorderedAccessViewDescription
        {
            Format = Format.R32_Typeless,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = 1, Flags = BufferUnorderedAccessViewFlags.Raw },
        };
    }
}
