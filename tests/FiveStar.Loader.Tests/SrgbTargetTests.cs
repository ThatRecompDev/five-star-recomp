// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text;
using FiveStar.Graphics;
using FiveStar.Graphics.Rendering;
using FiveStar.Loader.Tests.Support;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// The G-buffer's albedo on WARP: an sRGB render target whose desc also asks for UAV access, resting in
/// UNORDERED_ACCESS and moved to RENDER_TARGET, is output 0 of a two-target draw beside a UNORM target.
/// </summary>
public sealed unsafe class SrgbTargetTests
{
    private const int Size = 8;

    private const string Source = """
        #define RS "RootFlags(0)"
        [RootSignature(RS)]
        float4 vs(uint id : SV_VertexID) : SV_Position {
            float2 corner = float2((id << 1) & 2, id & 2);
            return float4(corner * float2(2, -2) + float2(-1, 1), 0, 1);
        }
        struct Out { float4 albedo : SV_Target0; float4 normal : SV_Target1; };
        [RootSignature(RS)]
        Out ps(float4 position : SV_Position) {
            Out o;
            o.albedo = float4(0.5, 0.25, 1.0, 0.1);
            o.normal = float4(0.25, 0.5, 0.75, 1.0);
            return o;
        }
        """;

    private const int BlobPointerSlot = 3;
    private const int BlobSizeSlot = 4;

    [WarpFact]
    public void Output_zero_lands_in_an_srgb_target_that_also_allows_uav()
    {
        using var gpu = GpuDevice.CreateWarp(debugLayer: false);
        var device = gpu.Device;
        using var context = new CommandContext(device);
        using var arena = new ResourceArena(device);
        using var heap = device.CreateDescriptorHeap(new Vortice.Direct3D12.DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 2));

        var albedoDescription = Vortice.Direct3D12.ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm_SRgb, Size, Size, 1, 1, flags: ResourceFlags.AllowRenderTarget | ResourceFlags.AllowUnorderedAccess);
        using var albedo = device.CreateCommittedResource(HeapType.Default, albedoDescription, ResourceStates.UnorderedAccess);
        using var normal = device.CreateCommittedResource(HeapType.Default, Vortice.Direct3D12.ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, Size, Size, 1, 1, flags: ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget);

        uint increment = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        var first = heap.GetCPUDescriptorHandleForHeapStart();
        var second = first + (int)increment;
        device.CreateRenderTargetView(albedo, null, first);
        device.CreateRenderTargetView(normal, null, second);

        var vertex = Compile("vs", "vs_5_1");
        var pixel = Compile("ps", "ps_5_1");
        using var root = device.CreateRootSignature(0, pixel);
        using var pipeline = device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = root,
            VertexShader = vertex,
            PixelShader = pixel,
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            SampleMask = uint.MaxValue,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = [Format.R8G8B8A8_UNorm_SRgb, Format.R8G8B8A8_UNorm],
            SampleDescription = new SampleDescription(1, 0),
        });

        var list = context.List;
        list.ResourceBarrierTransition(albedo, ResourceStates.UnorderedAccess, ResourceStates.RenderTarget);
        list.SetGraphicsRootSignature(root);
        list.SetPipelineState(pipeline);

        list.OMSetRenderTargets([first, second], null);
        list.RSSetViewport(new Vortice.Mathematics.Viewport(Size, Size));
        list.RSSetScissorRect(Size, Size);
        list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        list.DrawInstanced(3, 1, 0, 0);
        list.ResourceBarrierTransition(albedo, ResourceStates.RenderTarget, ResourceStates.UnorderedAccess);
        context.ExecuteAndWait();

        uint texel = ReadFirst(context, arena, albedo, ResourceStates.UnorderedAccess);

        // 0.5 in sRGB is 188; alpha 0.1 is 26.
        Assert.Equal(0x1AFFBCBCu & 0xFF0000FFu, texel & 0xFF0000FFu);
    }

    private static uint ReadFirst(CommandContext context, ResourceArena arena, ID3D12Resource texture, ResourceStates resting)
    {
        const int Pitch = 256;

        var readback = arena.ReadbackBuffer(Pitch * Size);
        var footprint = new PlacedSubresourceFootPrint { Footprint = new SubresourceFootPrint(texture.Description.Format, Size, Size, 1, Pitch) };
        context.List.ResourceBarrierTransition(texture, resting, ResourceStates.CopySource);
        context.List.CopyTextureRegion(new TextureCopyLocation(readback, footprint), 0, 0, 0, new TextureCopyLocation(texture, 0));
        context.List.ResourceBarrierTransition(texture, ResourceStates.CopySource, resting);
        context.ExecuteAndWait();

        void* mapped;
        readback.Map(0, null, &mapped).CheckError();
        uint value = *(uint*)mapped;
        readback.Unmap(0);

        return value;
    }

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern int D3DCompile(byte* source, nuint size, string? name, nint defines, nint include, string entry, string target, uint flags, uint effectFlags, out nint code, out nint errors);

    private static byte[] Compile(string entry, string profile)
    {
        byte[] text = Encoding.ASCII.GetBytes(Source);

        fixed (byte* source = text)
        {
            int hr = D3DCompile(source, (nuint)text.Length, null, 0, 0, entry, profile, 0, 0, out nint code, out nint errors);

            if (hr < 0)
            {
                throw new InvalidOperationException($"{entry}: 0x{hr:x8} {Marshal.PtrToStringAnsi(Blob(errors, BlobPointerSlot))}");
            }

            var bytes = new ReadOnlySpan<byte>((void*)Blob(code, BlobPointerSlot), (int)Blob(code, BlobSizeSlot)).ToArray();
            Marshal.Release(code);

            return bytes;
        }
    }

    private static nint Blob(nint blob, int slot)
    {
        return ((delegate* unmanaged<nint, nint>)(*(nint**)blob)[slot])(blob);
    }
}
