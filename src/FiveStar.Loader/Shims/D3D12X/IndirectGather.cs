// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

// The gather of indirect records clamped once per list follows victory-fork (github.com/fxceless/victory-fork,
// commit 465b94f, crates/victory-gpu/src/indirect.rs and draw.rs), itself built on VIctory; GPL-3.0-or-later. Ported as
// facts and written anew here.

using System.Collections.Concurrent;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Shims.D3D12X.States;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// <c>--indirect-gather</c>: an ExecuteIndirect whose records nothing earlier in its desktop list (segment) can have
/// written gets a slot in its list's gather buffer instead of a clamp pass in front of it; before the segment runs, a
/// prelude on a queue of the loader's own (<see cref="IndirectGatherPrelude"/>) copies and clamps every gathered record
/// in passes of up to <see cref="IndirectGatherProgram.RecordsPerPass"/>, with the bounds and the budget of
/// <see cref="IndirectClamp"/>. Gathering in a segment ends at its first barrier, cache flush, copy, memory write or
/// wait, dispatch, or draw or ExecuteIndirect of a pipeline whose programs store to memory (<see cref="PipelineStores"/>):
/// after any of them the arguments may come from the list's own work, and later records are clamped one by one as
/// before. Only records without a count buffer are gathered. The prelude reads the arguments when the segment is about
/// to run, so a list submitted again clamps them anew.
/// </summary>
internal static unsafe class IndirectGather
{
    /// <summary>Records one ExecuteIndirect may gather.</summary>
    public const uint MaxRecords = 64;

    /// <summary>Bytes in front of a gather buffer's records: its budget counter.</summary>
    private const ulong BudgetBytes = 256;
    private const ulong FirstBytes = 64 << 10;
    private const ulong Growth = 2;
    private const ulong RecordAlignment = 16;
    private const int DefaultHeap = 1;

    private static readonly ConcurrentDictionary<nint, GatherBatch> Batches = new();
    private static long _gathered;
    private static long _clamped;

    /// <summary>Whether <c>--indirect-gather</c> is on.</summary>
    public static bool Enabled { get; set; }

    /// <summary>ExecuteIndirects gathered and clamped one by one while gathering was on (for the log).</summary>
    public static (long Gathered, long Alone) Counts => (Interlocked.Read(ref _gathered), Interlocked.Read(ref _clamped));

    /// <summary>"; [indirect gather] G gathered, A alone, P preludes of N passes" for the thread sample; empty when off.</summary>
    public static string Summary
    {
        get
        {
            if (!Enabled)
            {
                return string.Empty;
            }

            var (gathered, alone) = Counts;

            return $"; [indirect gather] {gathered} ExecuteIndirects gathered, {alone} clamped alone, {IndirectGatherPrelude.Count} preludes of {IndirectGatherPrelude.Passes} passes";
        }
    }

    /// <summary>A new recording: gathering opens in its first segment, its records are dropped.</summary>
    [HotPath]
    public static void Reset(XboxObject* self)
    {
        if (!Enabled)
        {
            return;
        }

        var scratch = ListIndirectScratch.Of(self);
        scratch->GatherOpen = 1;
        scratch->GatherSegment = 0;

        // The buffer is written again by this recording's preludes, which run after the list's last run on its queue.
        if (Batches.TryGetValue((nint)self, out var batch))
        {
            batch.Records.Clear();
            batch.Used = BudgetBytes;
        }
    }

    /// <summary>The list split at a memory wait: gathering opens again in the new segment, which runs after the wait.</summary>
    [HotPath]
    public static void SegmentStarted(XboxObject* self)
    {
        if (Enabled)
        {
            var scratch = ListIndirectScratch.Of(self);
            scratch->GatherOpen = 1;
            scratch->GatherSegment = ListSegments.Of(self)->Count;
        }
    }

    /// <summary>Work or a barrier that may make later arguments come from the list itself: gathering ends in this segment.</summary>
    [HotPath]
    public static void Close(XboxObject* self)
    {
        if (Enabled)
        {
            ListIndirectScratch.Of(self)->GatherOpen = 0;
        }
    }

    /// <summary>After a draw or ExecuteIndirect recorded: a dispatch, or a pipeline that stores, ends gathering.</summary>
    [HotPath]
    public static void AfterWork(XboxObject* self, bool dispatch, nint pipeline)
    {
        if (Enabled && (dispatch || PipelineStores.Stores(pipeline)))
        {
            ListIndirectScratch.Of(self)->GatherOpen = 0;
        }
    }

    /// <summary>
    /// Gathers the ExecuteIndirect of <paramref name="call"/> over <paramref name="arguments"/> at
    /// <paramref name="offset"/> (no count buffer): its records' slots in the gather buffer to execute instead; false
    /// when it cannot be gathered (it is clamped alone then).
    /// </summary>
    [HotPath]
    public static bool Try(XboxObject* self, IndirectCall call, nint arguments, ulong offset, nint count, out nint buffer, out ulong slot)
    {
        buffer = 0;
        slot = 0;

        if (!Enabled)
        {
            return false;
        }

        if (!Gatherable(self, call, arguments, offset, count) || !Take(self, (ulong)call.Stride * call.MaxCount, out buffer, out slot))
        {
            Interlocked.Increment(ref _clamped);

            return false;
        }

        var batch = Batches[(nint)self];
        int segment = ListSegments.Of(self)->Count;

        for (uint i = 0; i < call.MaxCount; i++)
        {
            ulong record = (ulong)i * call.Stride;
            batch.Records.Add(new GatherRecord(buffer, arguments, (uint)(offset + record), (uint)(slot + record), call.Kind, call.Stride, segment));
        }

        Interlocked.Increment(ref _gathered);

        return true;
    }

    /// <summary>Whether <paramref name="list"/> has gathered records to clamp before it runs.</summary>
    [HotPath]
    public static bool Has(nint list)
    {
        return Enabled && Batches.TryGetValue(list, out var batch) && batch.Records.Count > 0;
    }

    /// <summary>Before segment <paramref name="segment"/> of <paramref name="list"/> runs on <paramref name="queue"/>: its gathered records clamped.</summary>
    public static void Before(XboxQueueObject* queue, nint list, int segment)
    {
        if (Enabled && Batches.TryGetValue(list, out var batch) && batch.Records.Count > 0)
        {
            IndirectGatherPrelude.Run(ShimContext.Runtime.D3D12X.Objects.DesktopDevice, queue->Header.Inner, batch.Records, segment);
        }
    }

    /// <summary>Whether the list's current segment may still gather records.</summary>
    [HotPath]
    internal static bool IsOpen(XboxObject* self)
    {
        var scratch = ListIndirectScratch.Of(self);

        return scratch->GatherOpen != 0 && scratch->GatherSegment == ListSegments.Of(self)->Count;
    }

    [HotPath]
    private static bool Gatherable(XboxObject* self, IndirectCall call, nint arguments, ulong offset, nint count)
    {
        bool open = IsOpen(self);
        ulong end = offset + ((ulong)call.Stride * call.MaxCount);

        return open && count == 0 && arguments != 0 && call.Kind >= 0 && call.MaxCount > 0 && call.MaxCount <= MaxRecords && end <= uint.MaxValue
            && IndirectGatherProgram.Ready(ShimContext.Runtime.D3D12X.Objects.DesktopDevice, out _, out _);
    }

    /// <summary>The next aligned piece of <paramref name="bytes"/> in the list's gather buffer, growing it when full.</summary>
    private static bool Take(XboxObject* self, ulong bytes, out nint buffer, out ulong offset)
    {
        var batch = Batches.GetOrAdd((nint)self, static _ => new GatherBatch());
        offset = (batch.Used + RecordAlignment - 1) & ~(RecordAlignment - 1);

        if (batch.Buffer == 0 || offset + bytes > batch.Bytes)
        {
            ulong size = Math.Max(FirstBytes, batch.Bytes * Growth);

            while (size < BudgetBytes + bytes)
            {
                size *= Growth;
            }

            nint device = ShimContext.Runtime.D3D12X.Objects.DesktopDevice;
            nint grown = DesktopCalls.CreateBuffer(device, new HeapProperties { Type = DefaultHeap }, size, DesktopCalls.UnorderedAccessFlag, ResourceStates.Common);

            if (grown == 0)
            {
                buffer = 0;

                return false;
            }

            // The outgrown buffer stays alive: records already gathered and commands recorded use it.
            batch.Buffer = grown;
            batch.Bytes = size;
            offset = BudgetBytes;
        }

        batch.Used = offset + bytes;
        buffer = batch.Buffer;

        return true;
    }
}
