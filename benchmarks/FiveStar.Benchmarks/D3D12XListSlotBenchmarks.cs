// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using FiveStar.Loader;
using FiveStar.Loader.Codegen;
using FiveStar.Loader.Shims.D3D12X;
using FiveStar.Loader.Shims.D3D12X.Binding;

namespace FiveStar.Benchmarks;

/// <summary>
/// The command-list slot handlers the game calls most, end to end on a wrapper whose desktop list is a stand-in (every
/// desktop slot returns at once): what our handler adds per draw, dispatch, root constant and pipeline. No device: the
/// list has no root signature, so draws upload no user data. BenchmarkDotNet derives a runner from the class, so it
/// cannot be sealed.
/// </summary>
[MemoryDiagnoser]
public unsafe class D3D12XListSlotBenchmarks : IDisposable
{
    private const int DesktopSlots = 160;
    private const int DesktopObjectBytes = 64;
    private const uint Vertices = 3;
    private const uint Constant = 7;
    private const uint TriangleList = 4;
    private const uint Values = 8;
    private const ulong Address = 0x2_0000_0000;

    private LoaderRuntime? _runtime;
    private nint* _desktopVtable;
    private nint _desktop;
    private XboxObject* _list;
    private delegate* unmanaged<XboxObject*, uint, uint, uint, uint, void> _draw;
    private delegate* unmanaged<XboxObject*, uint, uint, uint, void> _dispatch;
    private delegate* unmanaged<XboxObject*, uint, uint, uint, void> _constant;
    private delegate* unmanaged<XboxObject*, nint, void> _pipeline;
    private delegate* unmanaged<XboxObject*, uint, void> _topology;
    private delegate* unmanaged<XboxObject*, uint, uint, uint*, uint, void> _constants;
    private delegate* unmanaged<XboxObject*, uint, ulong, void> _address;
    private delegate* unmanaged<XboxObject*, nint, uint, nint, ulong, nint, ulong, void> _indirect;
    private uint* _values;

    [GlobalSetup]
    public void Setup()
    {
        _runtime = BenchmarkRuntime.Create();
        var code = new X64Emitter();
        code.Ret();
        nint ret = _runtime.CodeHeap.Write(code.ToArray());
        _runtime.CodeHeap.Seal();

        _desktopVtable = (nint*)NativeMemory.AllocZeroed(DesktopSlots, (nuint)sizeof(nint));

        for (int i = 0; i < DesktopSlots; i++)
        {
            _desktopVtable[i] = ret;
        }

        _desktop = (nint)NativeMemory.AllocZeroed(DesktopObjectBytes);
        *(nint**)_desktop = _desktopVtable;
        _list = (XboxObject*)NativeMemory.AllocZeroed((nuint)XboxInterfaces.CommandListObjectSize);
        _list->Inner = _desktop;

        _draw = &CommandListBindingMethods.DrawInstanced;
        _dispatch = &CommandListBindingMethods.Dispatch;
        _constant = &CommandListBindingMethods.SetGraphicsRoot32BitConstant;
        _pipeline = &RectangleListSlots.SetPipelineState;
        _topology = &PrimitiveTopologies.SetPrimitiveTopology;

        _constants = &CommandListBindingMethods.SetGraphicsRoot32BitConstants;
        _address = &CommandListBindingMethods.SetGraphicsRootAddress;
        _indirect = &CommandListBindingMethods.ExecuteIndirect;
        _values = (uint*)NativeMemory.AllocZeroed(Values, sizeof(uint));
    }

    [Benchmark]
    public void Draw()
    {
        _draw(_list, Vertices, 1, 0, 0);
    }

    [Benchmark]
    public void Dispatch()
    {
        _dispatch(_list, 1, 1, 1);
    }

    [Benchmark]
    public void RootConstant()
    {
        _constant(_list, 0, Constant, 0);
    }

    [Benchmark]
    public void SetPipeline()
    {
        _pipeline(_list, _desktop);
    }

    [Benchmark]
    public void SetTopology()
    {
        _topology(_list, TriangleList);
    }

    /// <summary>Eight root constants in one call (slots 39 and 40).</summary>
    [Benchmark]
    public void RootConstants()
    {
        _constants(_list, 0, Values, _values, 0);
    }

    /// <summary>A root CBV, SRV or UAV address (slots 41 to 46).</summary>
    [Benchmark]
    public void RootAddress()
    {
        _address(_list, 0, Address);
    }

    /// <summary>An ExecuteIndirect (slot 61) of a signature the shim did not see created: no clamp, the desktop call only.</summary>
    [Benchmark]
    public void ExecuteIndirect()
    {
        _indirect(_list, 0, 1, 0, 0, 0, 0);
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _runtime?.Dispose();
        NativeMemory.Free(_desktopVtable);
        NativeMemory.Free((void*)_desktop);
        NativeMemory.Free(_list);
        NativeMemory.Free(_values);
        GC.SuppressFinalize(this);
    }
}
