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
/// The passes that clamp a segment's gathered indirect records (<see cref="IndirectGather"/>), run on a direct queue of
/// the loader's own before the segment, as <see cref="Resources.DepthTwinPrelude"/> runs its copies: the game's queue
/// signals where its earlier work ends, ours waits for that, runs the passes and signals, and the game's queue waits for
/// that before the segment. Per gather buffer one pass zeroes its budget counter; then one pass per
/// <see cref="IndirectGatherProgram.RecordsPerPass"/> records of a source buffer, kind and stride. A prelude list is
/// reused once the GPU is past it.
/// </summary>
internal static unsafe class IndirectGatherPrelude
{
    private const int QueueSignalSlot = 14;
    private const int QueueWaitSlot = 15;

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
    private static readonly List<PreludeList> Free = [];
    private static readonly List<PreludeList> Running = [];
    private static readonly List<GatherRecord> Segment = [];
    private static ID3D12Device? _device;
    private static ID3D12CommandQueue? _queue;
    private static ID3D12Fence? _order;
    private static ID3D12Fence? _done;
    private static ulong _orderValue;
    private static ulong _doneValue;

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

            Ready(device);
            Segment.Sort(static (a, b) => Key(a).CompareTo(Key(b)));
            var prelude = Take();
            prelude.Allocator.Reset();
            prelude.List.Reset(prelude.Allocator, null);

            nint list = prelude.List.NativePointer;
            ((delegate* unmanaged<nint, nint, void>)ComObject.Slot(list, SetRootSignatureSlot))(list, root);
            ((delegate* unmanaged<nint, nint, void>)ComObject.Slot(list, SetPipelineSlot))(list, pipeline);

            ZeroBudgets(list);
            RecordPasses(list);
            prelude.List.Close();
            Submit(gameQueue, prelude);
            Count++;
        }
    }

    /// <summary>Drops the queue, fences and lists of the device they were made on: for tests, which make a device each.</summary>
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
            _order?.Dispose();
            _done?.Dispose();
            _device?.Dispose();

            (_queue, _order, _done, _device) = (null, null, null, null);
            (_orderValue, _doneValue) = (0, 0);
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

    private static void Ready(nint device)
    {
        if (_queue is not null)
        {
            return;
        }

        Marshal.AddRef(device);
        _device = new ID3D12Device(device);
        _queue = _device.CreateCommandQueue(new Vortice.Direct3D12.CommandQueueDescription(CommandListType.Direct));
        _order = _device.CreateFence(0);
        _done = _device.CreateFence(0);
        _queue.Name = "loader:indirect-gather";
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

    /// <summary>The game's queue signals where its work ends; ours waits, runs the prelude and signals; the game's waits.</summary>
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
