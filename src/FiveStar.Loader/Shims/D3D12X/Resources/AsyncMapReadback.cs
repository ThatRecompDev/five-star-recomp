// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Native;
using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// The readback of <see cref="MapReadback"/> without a wait: at a Map the row of the copy an earlier Map started (when
/// the GPU has finished it) goes to the game's memory, and a new copy starts on a private queue that waits on the GPU,
/// not on the CPU, for everything submitted so far. A synchronous readback made the game's thread wait for every queue
/// at each Map (once a frame for the exposure targets): the GPU ran dry while the thread then recorded the rest of the
/// frame (w121: 12 to 89 ms a frame with no queue busy, waits of up to 117 ms in the thread samples). The row is one Map
/// older than before; a copy still running at the next Map is not started again.
/// </summary>
internal sealed unsafe class AsyncMapReadback : IDisposable
{
    /// <summary>Copies in flight at most (one command list each).</summary>
    private const int Lists = 4;

    /// <summary>ID3D12CommandQueue::Wait (d3d12.h).</summary>
    private const int QueueWaitSlot = 15;

    private readonly object _gate = new();
    private readonly Dictionary<nint, MapReadbackTarget> _targets = [];
    private readonly ID3D12CommandAllocator?[] _allocators = new ID3D12CommandAllocator?[Lists];
    private readonly ID3D12GraphicsCommandList?[] _lists = new ID3D12GraphicsCommandList?[Lists];
    private readonly ulong[] _listValues = new ulong[Lists];
    private readonly PlacedSubresourceFootPrint[] _footprint = new PlacedSubresourceFootPrint[1];
    private readonly uint[] _rows = new uint[1];
    private readonly ulong[] _rowSize = new ulong[1];
    private ID3D12Device? _device;
    private ID3D12CommandQueue? _queue;
    private ID3D12Fence? _fence;
    private ulong _value;
    private long _started;
    private long _busy;

    /// <summary>Copies started, and Maps that started none because every list was in flight.</summary>
    public (long Started, long Busy) Counts => (Interlocked.Read(ref _started), Interlocked.Read(ref _busy));

    /// <summary>
    /// Writes the row of the last finished copy of <paramref name="resource"/> to <paramref name="destination"/>; false
    /// when no copy of it has finished yet.
    /// </summary>
    public bool TryServe(nint resource, byte* destination)
    {
        lock (_gate)
        {
            if (_fence is null || !_targets.TryGetValue(resource, out var target))
            {
                return false;
            }

            Collect(target);

            if (!target.HasRow)
            {
                return false;
            }

            target.Row.AsSpan().CopyTo(new Span<byte>(destination, target.RowBytes));

            return true;
        }
    }

    /// <summary>
    /// Starts a copy of <paramref name="resource"/> (resting in <paramref name="state"/>) that runs once the GPU has done
    /// everything submitted up to <paramref name="marks"/>; none while an earlier one of it runs.
    /// </summary>
    public void Start(nint device, nint resource, int state, (nint Fence, ulong Value)[] marks)
    {
        lock (_gate)
        {
            Open(device);
            Marshal.AddRef(resource);
            var texture = new ID3D12Resource(resource);
            var target = Target(resource, texture);
            int list = target is null || target.Pending != 0 ? -1 : FreeList();

            if (list < 0)
            {
                if (target is not null && target.Pending == 0)
                {
                    Interlocked.Increment(ref _busy);
                }

                texture.Dispose();

                return;
            }

            Record(list, target!, texture, (ResourceStates)state);

            foreach (var (fence, value) in marks)
            {
                if (fence != 0 && value != 0)
                {
                    _ = ((delegate* unmanaged<nint, nint, ulong, int>)ComObject.Slot(_queue!.NativePointer, QueueWaitSlot))(_queue.NativePointer, fence, value);
                }
            }

            _queue!.ExecuteCommandList(_lists[list]!);
            _value++;
            _queue.Signal(_fence!, _value).CheckError();

            _listValues[list] = _value;
            target!.Pending = _value;
            target.Source = texture;
            Interlocked.Increment(ref _started);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var target in _targets.Values)
            {
                target.Dispose();
            }

            _targets.Clear();

            for (int i = 0; i < Lists; i++)
            {
                _lists[i]?.Dispose();
                _allocators[i]?.Dispose();
            }

            _fence?.Dispose();
            _queue?.Dispose();
            _device?.Dispose();
        }
    }

    /// <summary>A finished copy's row taken into <see cref="MapReadbackTarget.Row"/>, its texture let go.</summary>
    private void Collect(MapReadbackTarget target)
    {
        if (target.Pending == 0 || _fence!.CompletedValue < target.Pending)
        {
            return;
        }

        void* data;
        target.Buffer.Map(0, null, &data).CheckError();
        new ReadOnlySpan<byte>((byte*)data + target.Footprint.Offset, target.RowBytes).CopyTo(target.Row);
        target.Buffer.Unmap(0);

        target.HasRow = true;
        target.Pending = 0;
        target.Source?.Dispose();
        target.Source = null;
    }

    /// <summary>The target of <paramref name="resource"/>, made (or made again for another texture at its address) when no copy runs; null when it cannot be read back.</summary>
    private MapReadbackTarget? Target(nint resource, ID3D12Resource texture)
    {
        var description = texture.Description;

        if (description.Dimension != ResourceDimension.Texture2D || description.SampleDescription.Count != 1)
        {
            return null;
        }

        _targets.TryGetValue(resource, out var target);

        if (target is not null)
        {
            Collect(target);

            if (target.Pending != 0 || (target.Width == description.Width && target.Format == (int)description.Format))
            {
                return target;
            }

            target.Dispose();
            _targets.Remove(resource);
        }

        _device!.GetCopyableFootprints(description, 0, 1, 0, _footprint, _rows, _rowSize, out ulong total);

        if (_rowSize[0] > MapReadback.MaxRowBytes)
        {
            return null;
        }

        var buffer = _device.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None, Vortice.Direct3D12.ResourceDescription.Buffer(total), ResourceStates.CopyDest);
        target = new MapReadbackTarget(buffer, _footprint[0], (int)_rowSize[0], description.Width, (int)description.Format);
        _targets[resource] = target;

        return target;
    }

    /// <summary>A command list whose last copy the GPU has finished; -1 when all are in flight.</summary>
    private int FreeList()
    {
        ulong completed = _fence!.CompletedValue;

        for (int i = 0; i < Lists; i++)
        {
            if (_listValues[i] <= completed)
            {
                return i;
            }
        }

        return -1;
    }

    private void Record(int index, MapReadbackTarget target, ID3D12Resource texture, ResourceStates resting)
    {
        if (_lists[index] is null)
        {
            _allocators[index] = _device!.CreateCommandAllocator(CommandListType.Direct);
            _lists[index] = _device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, _allocators[index]!, null);
        }
        else
        {
            _allocators[index]!.Reset();
            _lists[index]!.Reset(_allocators[index]!, null);
        }

        var list = _lists[index]!;
        bool transition = resting != ResourceStates.CopySource;

        if (transition)
        {
            list.ResourceBarrierTransition(texture, resting, ResourceStates.CopySource);
        }

        list.CopyTextureRegion(new TextureCopyLocation(target.Buffer, target.Footprint), 0, 0, 0, new TextureCopyLocation(texture, 0), null);

        if (transition)
        {
            list.ResourceBarrierTransition(texture, ResourceStates.CopySource, resting);
        }

        list.Close();
    }

    private void Open(nint device)
    {
        if (_device is not null)
        {
            return;
        }

        Marshal.AddRef(device);
        _device = new ID3D12Device(device);
        _queue = _device.CreateCommandQueue(new Vortice.Direct3D12.CommandQueueDescription(CommandListType.Direct));
        _fence = _device.CreateFence(0);
    }
}
