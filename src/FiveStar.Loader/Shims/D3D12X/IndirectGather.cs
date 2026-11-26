// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

// The gather of indirect records clamped once per list follows victory-fork (github.com/fxceless/victory-fork,
// commit 465b94f, crates/victory-gpu/src/indirect.rs and draw.rs), itself built on VIctory; GPL-3.0-or-later. Ported as
// facts and written anew here.

using System.Collections.Concurrent;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Shims.D3D12X.Present;
using FiveStar.Loader.Shims.D3D12X.States;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// <c>--indirect-gather</c>: an ExecuteIndirect whose records nothing earlier in its desktop list (segment) can have
/// written gets a slot in its list's gather buffer instead of a clamp pass in front of it; before the segment runs, a
/// prelude on the same queue (<see cref="IndirectGatherPrelude"/>) copies and clamps every gathered record in passes of
/// up to <see cref="IndirectGatherProgram.RecordsPerPass"/>, with the bounds and the budget of <see cref="IndirectClamp"/>.
/// Work that may write memory (a dispatch, a copy, a memory write, a draw or ExecuteIndirect of a pipeline whose programs
/// store, <see cref="PipelineStores"/>) marks the segment; the first barrier or cache flush after it ends gathering, as
/// arguments that work wrote can only be read after one; a memory wait the list is not split at ends it at once. Later
/// records are clamped one by one as before. Only records without a count buffer are gathered. The prelude reads the arguments when the segment is about
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

    /// <summary>Pipelines named in the summary as those whose work ended gathering.</summary>
    private const int TopClosers = 8;

    private static readonly ConcurrentDictionary<nint, GatherBatch> Batches = new();
    private static readonly ConcurrentDictionary<nint, long> Closers = new();
    private static readonly long[] Misses = new long[Enum.GetValues<IndirectGatherMiss>().Length];
    private static readonly long[] ClosedBy = new long[Enum.GetValues<IndirectGatherCloser>().Length];

    /// <summary>D3D12_RESOURCE_STATE_INDIRECT_ARGUMENT.</summary>
    private const int IndirectArgumentState = 0x200;
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

            return $"; [indirect gather] {gathered} ExecuteIndirects gathered, {alone} clamped alone ({MissSummary()}), {IndirectGatherPrelude.Count} preludes of {IndirectGatherPrelude.Passes} passes; segments whose gathering a barrier ended: {ClosedBySummary()}; ended most by the work of {CloserSummary()}";
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
        Opened(scratch);

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
            Opened(scratch);
        }
    }

    /// <summary>A memory wait the list was not split at: later arguments may come from the work it waits for, so gathering ends.</summary>
    [HotPath]
    public static void Close(XboxObject* self)
    {
        if (Enabled)
        {
            var scratch = ListIndirectScratch.Of(self);

            if (scratch->GatherOpen != 0)
            {
                scratch->GatherOpen = 0;
                scratch->GatherClosed = (int)IndirectGatherMiss.MemoryWait;
            }
        }
    }

    /// <summary>Work that may write memory (a dispatch, a copy, a memory write: <paramref name="writer"/>): a barrier after it ends gathering.</summary>
    [HotPath]
    public static void Wrote(XboxObject* self, IndirectGatherMiss writer)
    {
        if (!Enabled)
        {
            return;
        }

        // A copy or a memory write has no pipeline of its own; a dispatch is named by the one bound.
        nint pipeline = 0;

        if (writer == IndirectGatherMiss.AfterDispatch)
        {
            pipeline = RectangleListState.Of(self)->Pipeline;
        }

        Written(ListIndirectScratch.Of(self), writer, pipeline);
    }

    /// <summary>
    /// A barrier or cache flush: after one that follows work that may write, later arguments may be that work's (the
    /// game must order them so), and gathering ends in this segment; before any such work it changes nothing.
    /// </summary>
    [HotPath]
    public static void Synced(XboxObject* self, IndirectGatherCloser closer)
    {
        var scratch = ListIndirectScratch.Of(self);

        if (Enabled && scratch->GatherWritten != 0 && scratch->GatherOpen != 0)
        {
            scratch->GatherOpen = 0;
            scratch->GatherClosed = scratch->GatherWriter;
            Interlocked.Increment(ref ClosedBy[(int)closer]);
            NoteCloser(scratch->GatherWriterPipeline);
        }
    }

    /// <summary>As <see cref="Synced"/> for a ResourceBarrier of <paramref name="count"/> <paramref name="barriers"/>, told apart by what they do.</summary>
    [HotPath]
    public static void SyncedBy(XboxObject* self, uint count, TransitionBarrier* barriers)
    {
        if (!Enabled || ListIndirectScratch.Of(self)->GatherWritten == 0)
        {
            return;
        }

        var closer = IndirectGatherCloser.OtherTransition;

        for (uint i = 0; barriers != null && i < count; i++)
        {
            if (barriers[i].Type == TransitionBarrier.TransitionType && (barriers[i].StateAfter & IndirectArgumentState) != 0)
            {
                closer = IndirectGatherCloser.ArgumentTransition;

                break;
            }

            if (barriers[i].Type == TransitionBarrier.UavType)
            {
                closer = IndirectGatherCloser.UavBarrier;
            }
        }

        Synced(self, closer);
    }

    /// <summary>After a draw or ExecuteIndirect recorded: a dispatch, or a pipeline that stores, may have written.</summary>
    [HotPath]
    public static void AfterWork(XboxObject* self, bool dispatch, nint pipeline)
    {
        if (!Enabled || (!dispatch && !PipelineStores.Stores(pipeline)))
        {
            return;
        }

        var writer = IndirectGatherMiss.AfterIndirectDispatch;

        if (!dispatch && PipelineStores.Registered(pipeline))
        {
            writer = IndirectGatherMiss.AfterStoringDraw;
        }
        else if (!dispatch)
        {
            writer = IndirectGatherMiss.AfterUndecodedDraw;
        }

        Written(ListIndirectScratch.Of(self), writer, pipeline);
    }

    /// <summary>The segment's writer noted (the first one counts), so a barrier after it ends gathering.</summary>
    [HotPath]
    private static void Written(ListIndirectScratch* scratch, IndirectGatherMiss writer, nint pipeline)
    {
        if (scratch->GatherWritten == 0)
        {
            scratch->GatherWriter = (int)writer;
            scratch->GatherWriterPipeline = pipeline;
        }

        scratch->GatherWritten = 1;
    }

    /// <summary>A segment opened for gathering: nothing written in it yet.</summary>
    [HotPath]
    private static void Opened(ListIndirectScratch* scratch)
    {
        scratch->GatherWritten = 0;
        scratch->GatherWriter = 0;
        scratch->GatherWriterPipeline = 0;
        scratch->GatherClosed = 0;
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

        var miss = Miss(self, call, arguments, offset, count);

        if (miss == IndirectGatherMiss.None && !Take(self, (ulong)call.Stride * call.MaxCount, out buffer, out slot))
        {
            miss = IndirectGatherMiss.NoBuffer;
        }

        if (miss != IndirectGatherMiss.None)
        {
            Interlocked.Increment(ref _clamped);
            Interlocked.Increment(ref Misses[(int)miss]);

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

    /// <summary>Why the ExecuteIndirect cannot be gathered; <see cref="IndirectGatherMiss.None"/> when it can.</summary>
    [HotPath]
    private static IndirectGatherMiss Miss(XboxObject* self, IndirectCall call, nint arguments, ulong offset, nint count)
    {
        var scratch = ListIndirectScratch.Of(self);
        ulong end = offset + ((ulong)call.Stride * call.MaxCount);

        if (!IsOpen(self))
        {
            return Closed(scratch, ListSegments.Of(self)->Count);
        }

        if (count != 0)
        {
            return IndirectGatherMiss.CountBuffer;
        }

        if (call.MaxCount > MaxRecords)
        {
            return IndirectGatherMiss.TooManyRecords;
        }

        if (arguments == 0 || call.Kind < 0 || call.MaxCount == 0 || end > uint.MaxValue)
        {
            return IndirectGatherMiss.NotClampable;
        }

        if (!IndirectGatherProgram.Ready(ShimContext.Runtime.D3D12X.Objects.DesktopDevice, out _, out _))
        {
            return IndirectGatherMiss.ProgramNotReady;
        }

        return IndirectGatherMiss.None;
    }

    /// <summary>Why gathering ended in the list's segment <paramref name="segment"/>.</summary>
    [HotPath]
    private static IndirectGatherMiss Closed(ListIndirectScratch* scratch, int segment)
    {
        if (scratch->GatherSegment != segment || scratch->GatherClosed == 0)
        {
            return IndirectGatherMiss.OtherSegment;
        }

        return (IndirectGatherMiss)scratch->GatherClosed;
    }

    /// <summary>Counts the pipeline whose work ended a segment's gathering.</summary>
    private static void NoteCloser(nint pipeline)
    {
        Closers.AddOrUpdate(pipeline, 1, static (_, count) => count + 1);
    }

    /// <summary>"reason N, ..." over the misses counted, most first.</summary>
    private static string MissSummary()
    {
        var parts = Enum.GetValues<IndirectGatherMiss>()
            .Where(miss => miss != IndirectGatherMiss.None && Interlocked.Read(ref Misses[(int)miss]) > 0)
            .OrderByDescending(miss => Interlocked.Read(ref Misses[(int)miss]))
            .Select(miss => $"{miss} {Interlocked.Read(ref Misses[(int)miss])}");

        return string.Join(", ", parts);
    }

    /// <summary>"kind N, ..." over the barriers and flushes that ended gathering.</summary>
    private static string ClosedBySummary()
    {
        var parts = Enum.GetValues<IndirectGatherCloser>().Select(closer => $"{closer} {Interlocked.Read(ref ClosedBy[(int)closer])}");

        return string.Join(", ", parts);
    }

    /// <summary>The pipelines whose work ended gathering most often, by name.</summary>
    private static string CloserSummary()
    {
        var parts = Closers.OrderByDescending(entry => entry.Value).Take(TopClosers)
            .Select(entry => $"{QueueProgress.PipelineName(entry.Key)} x{entry.Value}");

        return string.Join(", ", parts);
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
