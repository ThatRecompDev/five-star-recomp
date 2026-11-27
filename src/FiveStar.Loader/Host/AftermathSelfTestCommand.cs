// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace FiveStar.Loader.Host;

/// <summary>
/// <c>--aftermath-selftest</c>: the loader's Aftermath path on a small case. Crash dumps are enabled as a boot enables
/// them (<see cref="AftermathCrashDumps.Start"/>, with the same feature switches), a device is made on the first NVIDIA
/// adapter and initialized, and a compute program writes through a root UAV at the GPU address of a buffer released
/// before the dispatch: a page fault, which removes this process's device only (no hang, no TDR). The removal is then
/// reported as a boot reports it (<see cref="AftermathCrashDumps.Report"/>). Exit code 0 only when a dump was written.
/// </summary>
internal static unsafe class AftermathSelfTestCommand
{
    private const int Passed = 0;
    private const int Failed = 1;

    /// <summary>PCI vendor id of NVIDIA.</summary>
    private const uint NvidiaVendor = 0x10DE;

    /// <summary>The released buffer and how far past its start the program writes: 256 MiB, 256 bytes per thread.</summary>
    private const ulong BufferBytes = 256UL << 20;
    private const uint ThreadStride = 256;
    private const uint GroupSize = 64;
    private const uint Groups = (uint)(BufferBytes / ThreadStride / GroupSize);

    /// <summary>How long the fence is waited for before the removal is reported anyway.</summary>
    private const int FenceMilliseconds = 10_000;

    private const string PipelineName = "selftest:page-fault-writer";

    private const string Source = """
        RWByteAddressBuffer target : register(u0);
        [RootSignature("UAV(u0)")]
        [numthreads(64, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID) { target.Store(id.x * 256u, id.x); }
        """;

    public static int Run(HostOptions options, LoaderLog log)
    {
        string directory = options.AftermathDirectory ?? Path.GetFullPath("aftermath");
        var features = new AftermathFeatures(options.AftermathMarkers, options.AftermathResources, options.AftermathShaderInfo);
        var started = DateTime.Now;
        var dumps = AftermathCrashDumps.Start(directory, options.AftermathLibrary, features, AppContext.BaseDirectory, Environment.GetEnvironmentVariable(AftermathLibrary.SdkVariable), log);

        if (dumps is null)
        {
            return Failed;
        }

        using var device = CreateDevice(log);

        if (device is null)
        {
            return Failed;
        }

        dumps.AttachDevice(device.NativePointer);
        log.Info($"  [d3dkmt] driver shader tables: {DriverShaderTables.Describe(device.NativePointer)}");

        try
        {
            Fault(device, dumps, log);
        }
        catch (Exception exception)
        {
            // Calls on the removed device fail (Vortice throws on DXGI_ERROR_DEVICE_REMOVED): the fault happened.
            log.Info($"  [aftermath] self-test: after the fault, {exception.GetType().Name}: {exception.Message}");
        }

        KernelGraphicsCalls.LogDeviceStates("the self-test's page fault");
        dumps.Report("the self-test's page fault");

        return Verdict(directory, started, log);
    }

    /// <summary>A device on the first NVIDIA adapter; null without one.</summary>
    private static ID3D12Device? CreateDevice(LoaderLog log)
    {
        using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);

        for (uint index = 0; factory.EnumAdapters1(index, out IDXGIAdapter1? adapter).Success; index++)
        {
            using (adapter)
            {
                var description = adapter!.Description1;

                if (description.VendorId != NvidiaVendor)
                {
                    continue;
                }

                log.Info($"  [aftermath] self-test on {description.Description}");

                return D3D12.D3D12CreateDevice<ID3D12Device>(adapter, FeatureLevel.Level_12_0);
            }
        }

        log.Error("[aftermath] self-test: no NVIDIA adapter");

        return null;
    }

    /// <summary>Dispatches the writer at the address of a released buffer and waits for the fence (it never comes).</summary>
    private static void Fault(ID3D12Device device, AftermathCrashDumps dumps, LoaderLog log)
    {
        byte[] code = Compile();
        using var root = device.CreateRootSignature(0, code);
        using var pipeline = device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = root, ComputeShader = code });
        QueueProgress.Name(pipeline.NativePointer, PipelineName);

        fixed (byte* bytes = code)
        {
            dumps.Shaders.Note(pipeline.NativePointer, PipelineStateSubObjectType.ComputeShader, bytes, code.Length);
        }

        ulong address = ReleasedAddress(device);
        using var queue = device.CreateCommandQueue(new Vortice.Direct3D12.CommandQueueDescription(CommandListType.Compute));
        using var allocator = device.CreateCommandAllocator(CommandListType.Compute);
        using var list = device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Compute, allocator, pipeline);

        list.SetComputeRootSignature(root);
        list.SetComputeRootUnorderedAccessView(0, address);
        list.Dispatch(Groups, 1, 1);
        list.Close();

        using var fence = device.CreateFence(0);
        using var done = new AutoResetEvent(false);
        queue.ExecuteCommandList(list);
        queue.Signal(fence, 1);
        fence.SetEventOnCompletion(1, done.SafeWaitHandle.DangerousGetHandle());
        log.Info($"  [aftermath] self-test: {Groups} groups of {PipelineName} write through freed GPU VA 0x{address:x}; fence signalled {done.WaitOne(FenceMilliseconds)}, removed reason {HResult.Format(device.DeviceRemovedReason.Code)}");
    }

    /// <summary>The GPU address of a committed UAV buffer, released before the GPU reads it.</summary>
    private static ulong ReleasedAddress(ID3D12Device device)
    {
        var description = Vortice.Direct3D12.ResourceDescription.Buffer(BufferBytes, ResourceFlags.AllowUnorderedAccess);
        using var buffer = device.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None, description, ResourceStates.Common);

        return buffer.GPUVirtualAddress;
    }

    /// <summary>The writer, compiled by d3dcompiler_47 (cs_5_1, its root signature inside).</summary>
    private static byte[] Compile()
    {
        byte[] source = Encoding.ASCII.GetBytes(Source);
        byte[] entry = Encoding.ASCII.GetBytes("main\0");
        byte[] target = Encoding.ASCII.GetBytes("cs_5_1\0");
        nint blob = 0;
        nint errors = 0;
        int hr;

        fixed (byte* text = source, main = entry, profile = target)
        {
            hr = IndirectClampNative.D3DCompile(text, (nuint)source.Length, null, 0, 0, main, profile, 0, 0, &blob, &errors);
        }

        if (errors != 0)
        {
            ComObject.Release(errors);
        }

        if (HResult.Failed(hr))
        {
            throw new InvalidOperationException($"the self-test program did not compile: {HResult.Format(hr)}");
        }

        byte* bytes = ((delegate* unmanaged<nint, byte*>)ComObject.Slot(blob, IndirectClampNative.BlobPointerSlot))(blob);
        nuint size = ((delegate* unmanaged<nint, nuint>)ComObject.Slot(blob, IndirectClampNative.BlobSizeSlot))(blob);
        byte[] code = new ReadOnlySpan<byte>(bytes, (int)size).ToArray();
        ComObject.Release(blob);

        return code;
    }

    /// <summary>Passed when a crash dump newer than the run is in the folder.</summary>
    private static int Verdict(string directory, DateTime started, LoaderLog log)
    {
        var folder = new DirectoryInfo(directory);
        FileInfo? dump = null;

        if (folder.Exists)
        {
            dump = folder.GetFiles("crash-*.nv-gpudmp").Where(file => file.LastWriteTime >= started.AddSeconds(-1)).OrderBy(file => file.LastWriteTime).LastOrDefault();
        }

        if (dump is null)
        {
            log.Error($"[aftermath] self-test FAILED: no crash-*.nv-gpudmp in {directory}");

            return Failed;
        }

        log.Info($"  [aftermath] self-test passed: {dump.FullName} ({dump.Length} bytes)");

        return Passed;
    }
}
