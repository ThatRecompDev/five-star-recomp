// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Shims.D3D12X.Present;
using FiveStar.Loader.Shims.D3D12X.States;
using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// The passes that clamp a segment's gathered indirect records (<see cref="IndirectGather"/>), a list of their own run
/// on the game's queue itself right before the segment. Per gather buffer one pass zeroes its budget counter; then one
/// pass per <see cref="IndirectGatherProgram.RecordsPerPass"/> records of a source buffer, kind and stride. Each game
/// queue has its own prelude lists (of its type) and a fence it signals after each, by which a list is reused once the
/// GPU is past it. The first form ran the preludes on one direct queue of the loader's own between a signal and a wait
/// of the game's queue: every game queue's preludes then queued behind each other there, so a queue waiting for another
/// game queue's work could hold that queue's prelude, and the other queue its own (w117 lost the device).
/// </summary>
internal static unsafe class IndirectGatherPrelude
{
    /// <summary>ID3D12CommandQueue: ExecuteCommandLists and Signal (d3d12.h).</summary>
    private const int QueueExecuteSlot = 10;
    private const int QueueSignalSlot = 14;

    /// <summary>ID3D12CommandQueue::GetDesc, a struct returned through a pointer (as <see cref="DebugLayer.QueueTrace"/> reads it).</summary>
    private const int QueueGetDescSlot = 18;

    /// <summary>ID3D12GraphicsCommandList: Dispatch, SetPipelineState, SetComputeRootSignature, SetComputeRoot32BitConstants, SetComputeRootShaderResourceView, SetComputeRootUnorderedAccessView.</summary>
    private const int DispatchSlot = 14;
    private const int SetPipelineSlot = 25;
    private const int SetRootSignatureSlot = 29;
    private const int SetConstantsSlot = 35;
    private const int SetShaderResourceSlot = 39;
    private const int SetUnorderedAccessSlot = 41;

    /// <summary>The header constants of a pass (kind, stride, records) and the constants per record (source and slot offsets).</summary>
    private const int KindConstant = 0;
    private const int StrideConstant = 1;
    private const int RecordsConstant = 2;
    private const uint ConstantsPerRecord = 2;

    private static readonly object Gate = new();
    private static readonly Dictionary<nint, QueuePreludes?> Queues = [];
    private static readonly List<GatherRecord> Segment = [];
    private static ID3D12Device? _device;

    /// <summary>Preludes run and passes recorded so far.</summary>
    public static long Count { get; private set; }

    public static long Passes { get; private set; }

    /// <summary>Records the passes for the records of <paramref name="records"/> in <paramref name="segment"/> and runs them before the next work on <paramref name="gameQueue"/>.</summary>
    public static void Run(nint device, nint gameQueue, List<GatherRecord> records, int segment)
    {
        lock (Gate)
        {
            Segment.Clear();
            Segment.AddRange(records.Where(record => record.Segment == segment));

            if (Segment.Count == 0 || !IndirectGatherProgram.Ready(device, out nint root, out nint pipeline))
            {
                return;
            }

            var queue = For(device, gameQueue);

            // A copy queue runs no dispatch; it runs no ExecuteIndirect either, so nothing is gathered for it.
            if (queue is null)
            {
                return;
            }

            Segment.Sort(static (a, b) => Key(a).CompareTo(Key(b)));
            var prelude = queue.Take(_device!);
            prelude.Allocator.Reset();
            prelude.List.Reset(prelude.Allocator, null);

            nint list = prelude.List.NativePointer;
            ((delegate* unmanaged<nint, nint, void>)ComObject.Slot(list, SetRootSignatureSlot))(list, root);
            ((delegate* unmanaged<nint, nint, void>)ComObject.Slot(list, SetPipelineSlot))(list, pipeline);

            ZeroBudgets(list);
            RecordPasses(list);
            prelude.List.Close();
            queue.Submit(gameQueue, prelude);
            Count++;
        }
    }

    /// <summary>Drops the queue, fences and lists of the device they were made on: for tests, which make a device each.</summary>
    internal static void Detach()
    {
        lock (Gate)
        {
            foreach (var queue in Queues.Values)
            {
                queue?.Dispose();
            }

            Queues.Clear();
            _device?.Dispose();
            _device = null;
            (Count, Passes) = (0, 0);
        }
    }

    /// <summary>The order passes take: by gather buffer, source buffer, kind and stride.</summary>
    private static (long, long, int, uint) Key(GatherRecord record)
    {
        return (record.Target, record.Source, record.Kind, record.Stride);
    }

    /// <summary>One pass per gather buffer zeroing its budget counter, then a UAV barrier before the clamps charge it.</summary>
    private static void ZeroBudgets(nint list)
    {
        uint* zero = stackalloc uint[(int)IndirectGatherProgram.Constants];
        zero[0] = IndirectGatherProgram.ZeroKind;
        nint last = 0;

        foreach (var record in Segment)
        {
            if (record.Target == last)
            {
                continue;
            }

            last = record.Target;
            ulong address = DesktopCalls.GpuAddress(last);
            Views(list, address, address);
            ((delegate* unmanaged<nint, uint, uint, uint*, uint, void>)ComObject.Slot(list, SetConstantsSlot))(list, IndirectGatherProgram.ConstantsParameter, IndirectGatherProgram.Constants, zero, 0);
            ((delegate* unmanaged<nint, uint, uint, uint, void>)ComObject.Slot(list, DispatchSlot))(list, 1, 1, 1);

            var barrier = new TransitionBarrier { Type = TransitionBarrier.UavType, Resource = last };
            BarrierWriter.Write(list, &barrier);
        }
    }

    /// <summary>The clamp passes: runs of records with one key, at most a pass's worth each.</summary>
    private static void RecordPasses(nint list)
    {
        uint* constants = stackalloc uint[(int)IndirectGatherProgram.Constants];
        int start = 0;

        while (start < Segment.Count)
        {
            var first = Segment[start];
            int end = start + 1;

            while (end < Segment.Count && end - start < IndirectGatherProgram.RecordsPerPass && Key(Segment[end]) == Key(first))
            {
                end++;
            }

            constants[KindConstant] = (uint)first.Kind;
            constants[StrideConstant] = first.Stride;
            constants[RecordsConstant] = (uint)(end - start);

            for (int i = start; i < end; i++)
            {
                uint at = IndirectGatherProgram.HeaderConstants + (ConstantsPerRecord * (uint)(i - start));
                constants[at] = Segment[i].From;
                constants[at + 1] = Segment[i].At;
            }

            Pass(list, first, constants, (uint)(end - start));
            start = end;
        }
    }

    private static void Pass(nint list, GatherRecord first, uint* constants, uint records)
    {
        ulong target = DesktopCalls.GpuAddress(first.Target);
        ((delegate* unmanaged<nint, uint, ulong, void>)ComObject.Slot(list, SetShaderResourceSlot))(list, IndirectGatherProgram.SourceParameter, DesktopCalls.GpuAddress(first.Source));
        Views(list, 0, target);
        ((delegate* unmanaged<nint, uint, uint, uint*, uint, void>)ComObject.Slot(list, SetConstantsSlot))(list, IndirectGatherProgram.ConstantsParameter, IndirectGatherProgram.Constants, constants, 0);
        ((delegate* unmanaged<nint, uint, uint, uint, void>)ComObject.Slot(list, DispatchSlot))(list, IndirectGatherProgram.Groups(records), 1, 1);
        Passes++;
    }

    /// <summary>The gather buffer as the clamped records and the budget counter (its first word); the source too when <paramref name="source"/> is given.</summary>
    private static void Views(nint list, ulong source, ulong target)
    {
        if (source != 0)
        {
            ((delegate* unmanaged<nint, uint, ulong, void>)ComObject.Slot(list, SetShaderResourceSlot))(list, IndirectGatherProgram.SourceParameter, source);
        }

        ((delegate* unmanaged<nint, uint, ulong, void>)ComObject.Slot(list, SetUnorderedAccessSlot))(list, IndirectGatherProgram.GatherParameter, target);
        ((delegate* unmanaged<nint, uint, ulong, void>)ComObject.Slot(list, SetUnorderedAccessSlot))(list, IndirectGatherProgram.BudgetParameter, target);
    }

    /// <summary>The preludes of the game's desktop queue <paramref name="gameQueue"/>, made at its first; null for a copy queue.</summary>
    private static QueuePreludes? For(nint device, nint gameQueue)
    {
        if (_device is null)
        {
            Marshal.AddRef(device);
            _device = new ID3D12Device(device);
        }

        if (!Queues.TryGetValue(gameQueue, out var queue))
        {
            D3D12X.CommandQueueDescription description;
            _ = ((delegate* unmanaged<nint, D3D12X.CommandQueueDescription*, D3D12X.CommandQueueDescription*>)ComObject.Slot(gameQueue, QueueGetDescSlot))(gameQueue, &description);
            var type = (CommandListType)description.Type;

            queue = null;

            if (type != CommandListType.Copy)
            {
                queue = new QueuePreludes(type, _device.CreateFence(0));
            }

            Queues[gameQueue] = queue;
        }

        return queue;
    }

    /// <summary>One game queue's prelude lists, of its type, and the fence it signals after each.</summary>
    private sealed class QueuePreludes(CommandListType type, ID3D12Fence fence) : IDisposable
    {
        private readonly List<PreludeList> _free = [];
        private readonly List<PreludeList> _running = [];
        private ulong _value;

        /// <summary>A list the GPU is past, or a new one.</summary>
        public PreludeList Take(ID3D12Device device)
        {
            ulong completed = fence.CompletedValue;

            for (int i = _running.Count - 1; i >= 0; i--)
            {
                if (_running[i].Value <= completed)
                {
                    _free.Add(_running[i]);
                    _running.RemoveAt(i);
                }
            }

            if (_free.Count > 0)
            {
                var reused = _free[^1];
                _free.RemoveAt(_free.Count - 1);

                return reused;
            }

            var allocator = device.CreateCommandAllocator(type);
            var list = device.CreateCommandList<ID3D12GraphicsCommandList>(0, type, allocator, null);
            list.Close();

            return new PreludeList(allocator, list);
        }

        /// <summary>The prelude on the game's queue, before what it runs next; then the fence that frees the list.</summary>
        public void Submit(nint gameQueue, PreludeList prelude)
        {
            nint list = prelude.List.NativePointer;
            ((delegate* unmanaged<nint, uint, nint*, void>)ComObject.Slot(gameQueue, QueueExecuteSlot))(gameQueue, 1, &list);

            _value++;
            _ = ((delegate* unmanaged<nint, nint, ulong, int>)ComObject.Slot(gameQueue, QueueSignalSlot))(gameQueue, fence.NativePointer, _value);
            prelude.Value = _value;
            _running.Add(prelude);
        }

        public void Dispose()
        {
            foreach (var prelude in _free.Concat(_running))
            {
                prelude.Dispose();
            }

            fence.Dispose();
        }
    }

    /// <summary>A prelude list and its allocator, and the done value after which the GPU is past it.</summary>
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
