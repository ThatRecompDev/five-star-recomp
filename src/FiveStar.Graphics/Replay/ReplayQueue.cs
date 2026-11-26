// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using Vortice.Direct3D12;

namespace FiveStar.Graphics.Replay;

/// <summary>A direct queue with one command list that is recorded, executed and waited for in turn.</summary>
internal sealed class ReplayQueue : IDisposable
{
    private readonly ID3D12Device _device;
    private readonly ID3D12CommandQueue _queue;
    private readonly ID3D12Fence _fence;
    private readonly ID3D12CommandAllocator _allocator;
    private ulong _value;

    public ReplayQueue(ID3D12Device device)
    {
        _device = device;
        _queue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
        _fence = device.CreateFence(0);
        _allocator = device.CreateCommandAllocator(CommandListType.Direct);
        List = device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, _allocator, null);
    }

    /// <summary>The list, open for recording.</summary>
    public ID3D12GraphicsCommandList List { get; }

    /// <summary>Ticks per second of the queue's timestamps.</summary>
    public ulong TimestampFrequency
    {
        get
        {
            _queue.GetTimestampFrequency(out ulong frequency).CheckError();

            return frequency;
        }
    }

    /// <summary>Closes and executes the list, waits for it and opens it again.</summary>
    public void Execute()
    {
        List.Close();
        _queue.ExecuteCommandList(List);
        _value++;
        _queue.Signal(_fence, _value).CheckError();

        while (_fence.CompletedValue < _value)
        {
            if (_device.DeviceRemovedReason.Failure)
            {
                throw new InvalidOperationException($"device removed during the replay: {_device.DeviceRemovedReason}");
            }

            Thread.Sleep(0);
        }

        _allocator.Reset();
        List.Reset(_allocator, null);
    }

    public void Dispose()
    {
        List.Dispose();
        _allocator.Dispose();
        _fence.Dispose();
        _queue.Dispose();
    }
}
