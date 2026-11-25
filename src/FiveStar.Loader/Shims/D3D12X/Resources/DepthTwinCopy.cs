// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Text;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Shims.D3D12X.Pipelines;
using FiveStar.Loader.Shims.D3D12X.States;
using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// Writes a depth twin's values into its depth texture (<see cref="DepthTwins"/>): a full-screen triangle whose pixel
/// program reads the twin's UAV at its pixel and outputs it as SV_Depth, depth test ALWAYS with writes, no colour
/// targets. A stencil twin goes into the stencil plane without SV_StencilRef (which not every desktop GPU has): the
/// stencil is cleared to 0, then one triangle per bit inverts that bit (write mask 1 &lt;&lt; bit) where the twin has it set
/// and discards elsewhere. Recording either replaces the list's graphics root signature, pipeline, targets, viewport and
/// topology: the caller sets the game's again.
/// </summary>
internal static unsafe class DepthTwinCopy
{
    private const string DepthSource = """
        RWTexture2D<float> twin : register(u0);
        float4 vs(uint id : SV_VertexID) : SV_Position {
            float2 corner = float2((id << 1) & 2, id & 2);
            return float4(corner * float2(2, -2) + float2(-1, 1), 0, 1);
        }
        [RootSignature("DescriptorTable(UAV(u0), visibility=SHADER_VISIBILITY_PIXEL)")]
        float ps(float4 position : SV_Position) : SV_Depth {
            return twin[uint2(position.xy)];
        }
        """;

    private const string StencilSource = """
        RWTexture2D<uint> twin : register(u0);
        cbuffer Plane : register(b0) { uint bit; };
        [RootSignature("DescriptorTable(UAV(u0), visibility=SHADER_VISIBILITY_PIXEL), RootConstants(num32BitConstants=1, b0, visibility=SHADER_VISIBILITY_PIXEL)")]
        void ps(float4 position : SV_Position) {
            if (((twin[uint2(position.xy)] >> bit) & 1u) == 0u) discard;
        }
        """;

    /// <summary>ID3D12GraphicsCommandList slots (d3d12.h).</summary>
    private const int DrawInstancedSlot = 12;
    private const int SetTopologySlot = 20;
    private const int SetViewportsSlot = 21;
    private const int SetScissorsSlot = 22;
    private const int SetPipelineSlot = 25;
    private const int SetGraphicsRootSignatureSlot = 30;
    private const int SetGraphicsTableSlot = 32;
    private const int SetGraphicsConstantSlot = 34;
    private const int SetRenderTargetsSlot = 46;
    private const int ClearDepthStencilSlot = 47;

    /// <summary>D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST, D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE.</summary>
    private const int TriangleList = 4;
    private const int TriangleType = 3;

    /// <summary>D3D12_RESOURCE_STATE_DEPTH_WRITE.</summary>
    private const int DepthWrite = 0x10;

    private const int FullScreenVertices = 3;
    private const int SampleMaskAll = -1;

    /// <summary>The stencil program's root parameters: the twin's table, then the bit.</summary>
    private const uint TableParameter = 0;
    private const uint BitParameter = 1;

    /// <summary>D3D12_CLEAR_FLAG_STENCIL; D3D12_STENCIL_OP_INVERT.</summary>
    private const int ClearStencil = 2;
    private const int StencilInvert = 6;

    /// <summary>Bits of a stencil value; the pipeline key of the depth copy (no bit).</summary>
    private const int StencilBits = 8;
    private const int DepthPlane = -1;

    /// <summary>ID3D12Device: CreateDescriptorHeap, GetDescriptorHandleIncrementSize, CreateDepthStencilView (d3d12.h).</summary>
    private const int CreateDescriptorHeapSlot = 14;
    private const int IncrementSlot = 15;
    private const int CreateDepthStencilViewSlot = 21;

    /// <summary>D3D12_DESCRIPTOR_HEAP_TYPE_DSV and D3D12_DSV_DIMENSION_TEXTURE2D; the copy's views (one per twin).</summary>
    private const uint DepthStencilHeapType = 3;
    private const int Texture2DView = 3;
    private const uint MaxViews = 256;

    private static readonly Guid DescriptorHeapIid = new("8efb471d-616c-4f49-90f7-127bb763fa51");
    private static readonly object Gate = new();
    private static nint _views;
    private static uint _increment;
    private static uint _used;
    private static readonly ConcurrentDictionary<(int Format, int Bit), nint> Pipelines = new();
    private static readonly CopyPrograms Depth = new(DepthSource, "depth_twin_copy");
    private static readonly CopyPrograms Stencil = new(StencilSource, "stencil_twin_copy");

    /// <summary>
    /// Records the copy of <paramref name="twin"/> into its plane of the depth through a writable depth-stencil view in
    /// <paramref name="format"/>; the depth is moved from <paramref name="state"/> to DEPTH_WRITE and back.
    /// <paramref name="table"/> is the twin UAV's GPU handle in the bound view heap; <paramref name="view"/>, the game's
    /// bound view, is not written through (it may be read-only). False when the programs could not be made.
    /// </summary>
    public static bool Record(nint device, nint list, DepthTwin twin, nint view, int format, int state, ulong table)
    {
        if (twin.Stencil)
        {
            return RecordStencil(device, list, twin, format, state, table);
        }

        nint pipeline = Pipeline(device, format, DepthPlane);
        view = WritableView(device, twin, format);

        if (pipeline == 0 || view == 0)
        {
            return false;
        }

        Begin(list, twin, view, state, table, Depth.Root);
        ((delegate* unmanaged<nint, nint, void>)ComObject.Slot(list, SetPipelineSlot))(list, pipeline);
        Draw(list);
        Move(list, twin.Depth, DepthWrite, state);

        return true;
    }

    /// <summary>The stencil twin into the stencil plane: a clear to 0, then one inverting triangle per bit.</summary>
    private static bool RecordStencil(nint device, nint list, DepthTwin twin, int format, int state, ulong table)
    {
        nint* pipelines = stackalloc nint[StencilBits];

        for (int bit = 0; bit < StencilBits; bit++)
        {
            pipelines[bit] = Pipeline(device, format, bit);

            if (pipelines[bit] == 0)
            {
                return false;
            }
        }

        nint view = WritableView(device, twin, format);

        if (view == 0)
        {
            return false;
        }

        Begin(list, twin, view, state, table, Stencil.Root);
        ((delegate* unmanaged<nint, nint, int, float, byte, uint, int*, void>)ComObject.Slot(list, ClearDepthStencilSlot))(list, view, ClearStencil, 0, 0, 0, null);

        for (uint bit = 0; bit < StencilBits; bit++)
        {
            ((delegate* unmanaged<nint, nint, void>)ComObject.Slot(list, SetPipelineSlot))(list, pipelines[bit]);
            ((delegate* unmanaged<nint, uint, uint, uint, void>)ComObject.Slot(list, SetGraphicsConstantSlot))(list, BitParameter, bit, 0);
            Draw(list);
        }

        Move(list, twin.Depth, DepthWrite, state);

        return true;
    }

    /// <summary>The twin's writes made visible, the depth in DEPTH_WRITE, and the copy's root, table, target, viewport and topology.</summary>
    private static void Begin(nint list, DepthTwin twin, nint view, int state, ulong table, nint root)
    {
        ResourceBarrierSlots.Uav(list, twin.Twin);
        Move(list, twin.Depth, state, DepthWrite);

        ((delegate* unmanaged<nint, nint, void>)ComObject.Slot(list, SetGraphicsRootSignatureSlot))(list, root);
        ((delegate* unmanaged<nint, uint, ulong, void>)ComObject.Slot(list, SetGraphicsTableSlot))(list, TableParameter, table);
        ((delegate* unmanaged<nint, uint, nint*, int, nint*, void>)ComObject.Slot(list, SetRenderTargetsSlot))(list, 0, null, 0, &view);

        float* viewport = stackalloc float[] { 0, 0, twin.Width, twin.Height, 0, 1 };
        int* scissor = stackalloc int[] { 0, 0, (int)twin.Width, (int)twin.Height };
        ((delegate* unmanaged<nint, uint, float*, void>)ComObject.Slot(list, SetViewportsSlot))(list, 1, viewport);
        ((delegate* unmanaged<nint, uint, int*, void>)ComObject.Slot(list, SetScissorsSlot))(list, 1, scissor);
        ((delegate* unmanaged<nint, int, void>)ComObject.Slot(list, SetTopologySlot))(list, TriangleList);
    }

    private static void Draw(nint list)
    {
        ((delegate* unmanaged<nint, uint, uint, uint, uint, void>)ComObject.Slot(list, DrawInstancedSlot))(list, FullScreenVertices, 1, 0, 0);
    }

    /// <summary>A writable depth-stencil view of the twin's depth in <paramref name="format"/>, made once in the copy's own heap; 0 when the heap is full.</summary>
    private static nint WritableView(nint device, DepthTwin twin, int format)
    {
        if (twin.CopyView != 0)
        {
            return twin.CopyView;
        }

        lock (Gate)
        {
            if (twin.CopyView != 0)
            {
                return twin.CopyView;
            }

            if (_views == 0)
            {
                uint* heap = stackalloc uint[] { DepthStencilHeapType, MaxViews, 0, 0 };
                Guid iid = DescriptorHeapIid;
                nint made = 0;
                _ = ((delegate* unmanaged<nint, uint*, Guid*, nint*, int>)ComObject.Slot(device, CreateDescriptorHeapSlot))(device, heap, &iid, &made);
                _views = made;
                _increment = ((delegate* unmanaged<nint, int, uint>)ComObject.Slot(device, IncrementSlot))(device, (int)DepthStencilHeapType);
            }

            if (_views == 0 || _used == MaxViews)
            {
                return 0;
            }

            nint handle = DesktopCalls.HeapCpuStart(_views) + (nint)(_used++ * _increment);

            // D3D12_DEPTH_STENCIL_VIEW_DESC: format, TEXTURE2D, no read-only flags, mip 0.
            int* description = stackalloc int[] { format, Texture2DView, 0, 0 };
            ((delegate* unmanaged<nint, nint, int*, nint, void>)ComObject.Slot(device, CreateDepthStencilViewSlot))(device, twin.Depth, description, handle);
            twin.CopyView = handle;

            return handle;
        }
    }

    private static void Move(nint list, nint resource, int before, int after)
    {
        if (before != after)
        {
            BarrierWriter.Transition(list, resource, before, after);
        }
    }

    /// <summary>The copy pipeline for depth-stencil views of <paramref name="format"/> and stencil bit <paramref name="bit"/> (the depth copy at <see cref="DepthPlane"/>); 0 when it cannot be made.</summary>
    private static nint Pipeline(nint device, int format, int bit)
    {
        if (Pipelines.TryGetValue((format, bit), out var made))
        {
            return made;
        }

        lock (Gate)
        {
            if (Pipelines.TryGetValue((format, bit), out made))
            {
                return made;
            }

            made = Make(device, format, bit);
            Pipelines[(format, bit)] = made;

            return made;
        }
    }

    private static nint Make(nint device, int format, int bit)
    {
        var programs = Depth;

        if (bit != DepthPlane)
        {
            programs = Stencil;
        }

        if (!Depth.Ready(device, withVertex: true) || !programs.Ready(device, withVertex: false))
        {
            return 0;
        }

        nint device2 = 0;
        Guid device2Iid = DesktopDevice.Device2Iid;

        if (HResult.Failed(DesktopDevice.QueryInterface(device, &device2Iid, &device2)))
        {
            return 0;
        }

        try
        {
            return Create(device2, programs, format, bit);
        }
        finally
        {
            DesktopDevice.Release(device2);
        }
    }

    private static nint Create(nint device2, CopyPrograms programs, int format, int bit)
    {
        using var stream = new DesktopStream();
        stream.AddPointer(PipelineStateSubObjectType.RootSignature, programs.Root);
        stream.AddShader(PipelineStateSubObjectType.VertexShader, Depth.Vertex!);
        stream.AddShader(PipelineStateSubObjectType.PixelShader, programs.Pixel!);

        stream.AddBytes(PipelineStateSubObjectType.Blend, StatePadding.Blend(DesktopStateBytes.DefaultBlend()));
        stream.AddWords(PipelineStateSubObjectType.SampleMask, [SampleMaskAll]);
        stream.AddBytes(PipelineStateSubObjectType.Rasterizer, Rasterizer());
        stream.AddBytes(PipelineStateSubObjectType.DepthStencil1, StatePadding.DepthStencil(DepthStencil(bit)));
        stream.AddWords(PipelineStateSubObjectType.PrimitiveTopology, [TriangleType]);

        stream.AddWords(PipelineStateSubObjectType.RenderTargetFormats, new int[PipelineLayout.RenderTargetSlots + 1]);
        stream.AddWords(PipelineStateSubObjectType.DepthStencilFormat, [format]);
        stream.AddWords(PipelineStateSubObjectType.SampleDescription, [1, 0]);

        Guid iid = DesktopDevice.PipelineStateIid;
        nint pipeline = 0;
        int hr = stream.Create(device2, &iid, &pipeline);

        if (HResult.Failed(hr))
        {
            ShimContext.Log?.Warn($"[d3d12_xs] depth twins: no copy pipeline for depth format {format}, stencil bit {bit} ({HResult.Format(hr)})");

            return 0;
        }

        QueueProgress.Name(pipeline, $"loader:depth-twin-copy format {format} bit {bit}");

        return pipeline;
    }

    /// <summary>Solid, no culling, depth clip on.</summary>
    private static byte[] Rasterizer()
    {
        var rasterizer = DesktopStateBytes.DefaultRasterizer();
        BitConverter.TryWriteBytes(rasterizer.AsSpan(DesktopStateBytes.CullModeField), DesktopStateBytes.CullNone);

        return rasterizer;
    }

    /// <summary>
    /// The depth copy: depth on with writes, test ALWAYS, stencil off. A stencil bit: depth off, stencil on, test ALWAYS,
    /// the pass op INVERT on that bit alone.
    /// </summary>
    private static byte[] DepthStencil(int bit)
    {
        var depth = DesktopStateBytes.DefaultDepthStencil();
        BitConverter.TryWriteBytes(depth.AsSpan(DesktopStateBytes.DepthFuncField), DesktopStateBytes.ComparisonAlways);

        if (bit == DepthPlane)
        {
            return depth;
        }

        BitConverter.TryWriteBytes(depth.AsSpan(DesktopStateBytes.DepthEnableField), 0);
        BitConverter.TryWriteBytes(depth.AsSpan(DesktopStateBytes.StencilEnableField), 1);
        depth[DesktopStateBytes.StencilWriteMaskField] = (byte)(1 << bit);

        int[] face = [DesktopStateBytes.StencilKeep, DesktopStateBytes.StencilKeep, StencilInvert, DesktopStateBytes.ComparisonAlways];
        DesktopStateBytes.WriteFace(depth, DesktopStateBytes.FrontFaceField, face);
        DesktopStateBytes.WriteFace(depth, DesktopStateBytes.BackFaceField, face);

        return depth;
    }

    private static byte[]? Compile(string text, string fileName, string entryName, string profileName)
    {
        byte[] source = Encoding.ASCII.GetBytes(text);
        byte[] entry = Encoding.ASCII.GetBytes(entryName + "\0");
        byte[] target = Encoding.ASCII.GetBytes(profileName + "\0");
        byte[] name = Encoding.ASCII.GetBytes(fileName + "\0");

        nint code = 0;
        nint errors = 0;
        int hr;

        fixed (byte* sourceText = source, main = entry, profile = target, file = name)
        {
            hr = IndirectClampNative.D3DCompile(sourceText, (nuint)source.Length, file, 0, 0, main, profile, 0, 0, &code, &errors);
        }

        if (errors != 0)
        {
            byte* message = ((delegate* unmanaged<nint, byte*>)ComObject.Slot(errors, IndirectClampNative.BlobPointerSlot))(errors);
            nuint length = ((delegate* unmanaged<nint, nuint>)ComObject.Slot(errors, IndirectClampNative.BlobSizeSlot))(errors);
            ShimContext.Log?.Warn($"[d3d12_xs] depth twins: {Encoding.ASCII.GetString(message, (int)length).Trim()}");
            ComObject.Release(errors);
        }

        if (HResult.Failed(hr) || code == 0)
        {
            return null;
        }

        byte* bytes = ((delegate* unmanaged<nint, byte*>)ComObject.Slot(code, IndirectClampNative.BlobPointerSlot))(code);
        nuint size = ((delegate* unmanaged<nint, nuint>)ComObject.Slot(code, IndirectClampNative.BlobSizeSlot))(code);
        var program = new ReadOnlySpan<byte>(bytes, (int)size).ToArray();
        ComObject.Release(code);

        return program;
    }

    /// <summary>One copy's programs and root signature (from the pixel program), compiled once by d3dcompiler_47.</summary>
    private sealed class CopyPrograms(string source, string name)
    {
        private bool _failed;

        public nint Root { get; private set; }

        public byte[]? Vertex { get; private set; }

        public byte[]? Pixel { get; private set; }

        public bool Ready(nint device, bool withVertex)
        {
            if (Root != 0)
            {
                return true;
            }

            if (_failed)
            {
                return false;
            }

            if (withVertex)
            {
                Vertex = Compile(source, name, "vs", "vs_5_1");
            }

            Pixel = Compile(source, name, "ps", "ps_5_1");
            nint root = 0;
            _failed = Pixel is null || (withVertex && Vertex is null);

            if (!_failed)
            {
                fixed (byte* blob = Pixel)
                {
                    _failed = HResult.Failed(DesktopDevice.CreateRootSignature(device, blob, (nuint)Pixel!.Length, &root));
                }
            }

            Root = root;

            return !_failed;
        }
    }
}
