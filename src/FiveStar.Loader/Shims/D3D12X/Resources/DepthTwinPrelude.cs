// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Native;
using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// The copies a submitted list needs between a depth twin's sides first (<see cref="DepthTwinSides.Plan"/>), run on a
/// direct queue of the loader's own, so the list may run on any queue: the game's queue signals where its earlier work
/// ends, the loader's queue waits for that, runs the copies and signals, and the game's queue waits for that before the
/// list. Between lists every texture is at home (<see cref="States.ResourceBarrierSlots"/>), the twin in
/// UNORDERED_ACCESS. A copy list is reused once the GPU is past it. Follows the prelude of the fork of VIctory by Kelvaroq
/// (victory-fork, crates/victory-gpu/src/host.rs; GPL-3.0-or-later), ported as facts and rewritten here.
/// </summary>
internal static unsafe class DepthTwinPrelude
{
    /// <summary>ID3D12CommandQueue::Signal and ::Wait in the desktop vtable (d3d12.h).</summary>
    private const int QueueSignalSlot = 14;
    private const int QueueWaitSlot = 15;

    private static readonly object Gate = new();
    private static readonly List<PreludeList> Free = [];
    private static readonly List<PreludeList> Running = [];
    private static ID3D12Device? _device;
    private static ID3D12CommandQueue? _queue;
    private static ID3D12Fence? _order;
    private static ID3D12Fence? _done;
    private static ID3D12DescriptorHeap? _views;
    private static uint _increment;
    private static uint _slots;
    private static ulong _orderValue;
    private static ulong _doneValue;

    /// <summary>Twins the prelude's own view heap holds a UAV of.</summary>
    private const uint ViewSlots = 256;

    /// <summary>Preludes logged one by one; after them every <see cref="LogEvery"/>-th.</summary>
    private const long LoggedFirst = 32;
    private const long LogEvery = 1000;

    /// <summary>Preludes run so far.</summary>
    public static long Count { get; private set; }

    /// <summary>Copies the preludes recorded: the plane into the twin, the twin into the plane, and copies left out.</summary>
    public static long ToTwin { get; private set; }

    public static long ToPlane { get; private set; }

    public static long Skipped { get; private set; }

    /// <summary>
    /// Runs <paramref name="copies"/> before the next work submitted to the game's desktop queue <paramref name="gameQueue"/>;
    /// false when they could not be recorded (no copies run then).
    /// </summary>
    public static bool Run(nint device, nint gameQueue, List<DepthTwinSides.Copy> copies)
    {
        lock (Gate)
        {
            if (!Ready(device))
            {
                return false;
            }

            var prelude = Take();
            prelude.Allocator.Reset();
            prelude.List.Reset(prelude.Allocator, null);

            foreach (var copy in copies)
            {
                Record(device, prelude.List.NativePointer, copy);
            }

            prelude.List.Close();
            Submit(gameQueue, prelude);
            Count++;
            Log(gameQueue, copies);

            return true;
        }
    }

    /// <summary>Drops the queue, fences and lists, which belong to the device they were made on: for tests, which make a device each.</summary>
    internal static void Detach()
    {
        lock (Gate)
        {
            foreach (var prelude in Free.Concat(Running))
            {
                prelude.Dispose();
            }

            Free.Clear();
            Running.Clear();
            _queue?.Dispose();
            _views?.Dispose();

            _order?.Dispose();
            _done?.Dispose();
            _device?.Dispose();

            _queue = null;
            _views = null;
            _slots = 0;

            ToTwin = 0;
            ToPlane = 0;
            Skipped = 0;

            _order = null;
            _done = null;
            _device = null;
            _orderValue = 0;
            _doneValue = 0;
        }
    }

    private static bool Ready(nint device)
    {
        if (_queue is not null)
        {
            return true;
        }

        Marshal.AddRef(device);
        _device = new ID3D12Device(device);
        _queue = _device.CreateCommandQueue(new Vortice.Direct3D12.CommandQueueDescription(CommandListType.Direct));
        _order = _device.CreateFence(0);
        _done = _device.CreateFence(0);
        _queue.Name = "loader:depth-twin-prelude";

        _views = _device.CreateDescriptorHeap(new Vortice.Direct3D12.DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, ViewSlots, DescriptorHeapFlags.ShaderVisible));
        _increment = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        return true;
    }

    /// <summary>A list the GPU is past, or a new one.</summary>
    private static PreludeList Take()
    {
        ulong completed = _done!.CompletedValue;

        for (int i = Running.Count - 1; i >= 0; i--)
        {
            if (Running[i].Value <= completed)
            {
                Free.Add(Running[i]);
                Running.RemoveAt(i);
            }
        }

        if (Free.Count > 0)
        {
            var reused = Free[^1];
            Free.RemoveAt(Free.Count - 1);

            return reused;
        }

        var allocator = _device!.CreateCommandAllocator(CommandListType.Direct);
        var list = _device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, allocator, null);
        list.Close();

        return new PreludeList(allocator, list);
    }

    private static void Record(nint device, nint list, DepthTwinSides.Copy copy)
    {
        var twin = copy.Twin;

        if (!copy.ToPlane)
        {
            if (DepthTwinFill.Record(device, list, twin, twin.Home, ~0))
            {
                ToTwin++;
            }
            else
            {
                Skipped++;
            }

            return;
        }

        ToPlane++;

        if (twin.DepthFormat == 0 || !Slot(twin))
        {
            Skipped++;

            return;
        }

        // The twin is read through a view of ours: the game may have written another view where the twin's was.
        nint heap = _views!.NativePointer;
        ((delegate* unmanaged<nint, uint, nint*, void>)ComObject.Slot(list, DepthTwinCopy.SetDescriptorHeapsSlot))(list, 1, &heap);
        ulong table = _views.GetGPUDescriptorHandleForHeapStart().Ptr + ((ulong)twin.PreludeSlot * _increment);
        DepthTwinCopy.Record(device, list, twin, 0, twin.DepthFormat, twin.Home, table);
    }

    /// <summary>The twin's UAV in the prelude's own heap, made at its first copy; false when the heap is full.</summary>
    private static bool Slot(DepthTwin twin)
    {
        if (twin.PreludeSlot >= 0)
        {
            return true;
        }

        if (_slots == ViewSlots)
        {
            return false;
        }

        Marshal.AddRef(twin.Twin);
        using var resource = new ID3D12Resource(twin.Twin);
        var handle = _views!.GetCPUDescriptorHandleForHeapStart() + (int)(_slots * _increment);
        var view = new UnorderedAccessViewDescription { Format = (Vortice.DXGI.Format)twin.Format, ViewDimension = UnorderedAccessViewDimension.Texture2D };
        _device!.CreateUnorderedAccessView(resource, null, view, handle);

        twin.PreludeSlot = (int)_slots;
        _slots++;

        return true;
    }

    /// <summary>The first preludes one by one, then every <see cref="LogEvery"/>-th, with the copies by direction and twin.</summary>
    private static void Log(nint gameQueue, List<DepthTwinSides.Copy> copies)
    {
        if (Count > LoggedFirst && Count % LogEvery != 0)
        {
            return;
        }

        string what = string.Join(", ", copies.Select(copy => $"{Direction(copy)} (twin 0x{copy.Twin.Twin:x} format {copy.Twin.Format} of depth 0x{copy.Twin.Depth:x}, {copy.Twin.Width}x{copy.Twin.Height})"));
        ShimContext.Log?.Info($"  [d3d12_xs] depth twins: prelude {Count} before queue 0x{gameQueue:x}: {what}; so far {ToTwin} plane->twin, {ToPlane} twin->plane, {Skipped} left out");
    }

    private static string Direction(DepthTwinSides.Copy copy)
    {
        if (copy.ToPlane)
        {
            return "twin->plane";
        }

        return "plane->twin";
    }

    /// <summary>The game's queue: signals where its work ends; ours waits, runs the prelude, signals; the game's waits.</summary>
    private static void Submit(nint gameQueue, PreludeList prelude)
    {
        _orderValue++;
        _ = ((delegate* unmanaged<nint, nint, ulong, int>)ComObject.Slot(gameQueue, QueueSignalSlot))(gameQueue, _order!.NativePointer, _orderValue);
        _queue!.Wait(_order, _orderValue);
        _queue.ExecuteCommandList(prelude.List);

        _doneValue++;
        _queue.Signal(_done!, _doneValue);
        prelude.Value = _doneValue;
        Running.Add(prelude);
        _ = ((delegate* unmanaged<nint, nint, ulong, int>)ComObject.Slot(gameQueue, QueueWaitSlot))(gameQueue, _done!.NativePointer, _doneValue);
    }

    /// <summary>A copy list and its allocator, and the done value after which the GPU is past it.</summary>
    private sealed class PreludeList(ID3D12CommandAllocator allocator, ID3D12GraphicsCommandList list) : IDisposable
    {
        public ID3D12CommandAllocator Allocator { get; } = allocator;

        public ID3D12GraphicsCommandList List { get; } = list;

        public ulong Value { get; set; }

        public void Dispose()
        {
            List.Dispose();
            Allocator.Dispose();
        }
    }
}
