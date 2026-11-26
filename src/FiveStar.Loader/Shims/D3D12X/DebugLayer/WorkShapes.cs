// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using FiveStar.Loader.Native;
using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// With <c>--gpu-timings</c>: how much work each pipeline's dispatches and indirect executions ask for, so a pass's time
/// can be told apart from an oversized dispatch (CS_ScreenProbes_GenerateShaderBins took 32-36 ms of every w94c frame
/// for a 383-instruction program). A Dispatch's group counts are summed on the CPU. An ExecuteIndirect's records are
/// written by the GPU: the first <see cref="SamplesPerFrame"/> clamped executions of each pipeline per frame copy their
/// first clamped record (<see cref="IndirectClamp"/>) into a readback slot, read at the next report once settled.
/// The report lists the passes <see cref="GpuTimings.Report"/> shows.
/// </summary>
internal static unsafe class WorkShapes
{
    /// <summary>Readback slots: two halves, one per report interval, so a report reads the half the next one does not write.</summary>
    private const int Slots = 4096;
    private const int HalfSlots = Slots / 2;
    private const int SlotBytes = 16;
    private const int RecordBytes = 12;
    private const int SamplesPerFrame = 2;

    private const long DefaultSettleMilliseconds = 2000;
    private const int Halves = 2;

    /// <summary>A copy recorded this recently may not have run yet: the report leaves it out (tests: 0).</summary>
    public static long SettleMilliseconds { get; set; } = DefaultSettleMilliseconds;

    /// <summary>ID3D12GraphicsCommandList::CopyBufferRegion (d3d12.h).</summary>
    private const int CopyBufferRegionSlot = 15;

    private static readonly ConcurrentDictionary<nint, Tally> Tallies = new();
    private static readonly Sample[] Samples = new Sample[Slots];
    private static ID3D12Resource? _readback;
    private static uint* _values;
    private static int _half;
    private static int _taken;

    /// <summary>After device creation, with the timings: the readback slots.</summary>
    public static void Attach(ID3D12Device desktop)
    {
        _readback = desktop.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None, Vortice.Direct3D12.ResourceDescription.Buffer(Slots * SlotBytes), Vortice.Direct3D12.ResourceStates.CopyDest);
        void* data;
        _readback.Map(0, null, &data).CheckError();

        _values = (uint*)data;
    }

    /// <summary>Drops the slots and every tally.</summary>
    public static void Detach()
    {
        _readback?.Dispose();
        _readback = null;
        _values = null;
        _half = 0;
        _taken = 0;

        Tallies.Clear();
        Array.Clear(Samples);
    }

    /// <summary>A Dispatch of <paramref name="x"/> x <paramref name="y"/> x <paramref name="z"/> groups with <paramref name="pipeline"/>.</summary>
    [HotPath]
    public static void Dispatch(nint pipeline, uint x, uint y, uint z)
    {
        if (_values == null)
        {
            return;
        }

        // Counters are atomic; the largest dispatch may tear between two threads (a diagnostic, read at reports).
        var tally = Of(pipeline);
        long groups = (long)x * y * z;
        Interlocked.Increment(ref tally.Dispatches);
        Interlocked.Add(ref tally.Groups, groups);

        if (groups >= Volatile.Read(ref tally.LargestGroups))
        {
            tally.LargestGroups = groups;
            tally.Largest = (x, y, z);
        }
    }

    /// <summary>
    /// An ExecuteIndirect of up to <paramref name="maxCount"/> records with <paramref name="pipeline"/> in frame
    /// <paramref name="frame"/>, recorded into desktop list <paramref name="list"/>; when its records were clamped into
    /// <paramref name="scratch"/> at <paramref name="offset"/> (COMMON again), a sampled one copies the first record.
    /// </summary>
    [HotPath]
    public static void Indirect(nint list, nint pipeline, int kind, uint maxCount, nint scratch, ulong offset, long frame)
    {
        if (_values == null)
        {
            return;
        }

        var tally = Of(pipeline);
        Interlocked.Increment(ref tally.Indirect);

        if (maxCount > tally.MaxRecords)
        {
            tally.MaxRecords = maxCount;
        }

        if (Interlocked.Exchange(ref tally.Frame, frame) != frame)
        {
            Volatile.Write(ref tally.Sampled, 0);
        }

        if (scratch == 0 || Interlocked.Increment(ref tally.Sampled) > SamplesPerFrame)
        {
            return;
        }

        int taken = Interlocked.Increment(ref _taken) - 1;

        if (taken >= HalfSlots)
        {
            return;
        }

        int slot = (Volatile.Read(ref _half) * HalfSlots) + taken;
        Samples[slot] = new Sample(pipeline, kind, Environment.TickCount64);
        ClampBarriers.BeforeCopy(list, scratch);
        ((delegate* unmanaged<nint, nint, ulong, nint, ulong, ulong, void>)ComObject.Slot(list, CopyBufferRegionSlot))(list, _readback!.NativePointer, (ulong)slot * SlotBytes, scratch, offset, RecordBytes);
        ClampBarriers.AfterCopy(list, scratch);
    }

    /// <summary>The pipeline's tally, made at its first work.</summary>
    private static Tally Of(nint pipeline)
    {
        return Tallies.GetOrAdd(pipeline, static _ => new Tally());
    }

    /// <summary>
    /// "; work per frame: pass: D dispatches, G groups (largest X x Y x Z), I indirect (up to R records, first record
    /// A x B x C in S samples)" for each of <paramref name="passes"/>, over <paramref name="frames"/> frames; then starts over.
    /// </summary>
    public static string Report(long frames, IReadOnlyCollection<string> passes, Func<nint, string> name)
    {
        if (_values == null)
        {
            return string.Empty;
        }

        int half = Volatile.Read(ref _half);
        Volatile.Write(ref _half, (half + 1) % Halves);
        int taken = Math.Min(Interlocked.Exchange(ref _taken, 0), HalfSlots);
        var shapes = new Dictionary<string, Shape>();

        foreach (var (pipeline, tally) in Tallies)
        {
            string pass = name(pipeline);

            if (passes.Contains(pass))
            {
                Of(shapes, pass).Add(tally);
            }

            tally.Clear();
        }

        AddSamples(shapes, passes, name, half * HalfSlots, taken);

        var text = new StringBuilder();
        string separator = "; work per frame: ";

        foreach (var (pass, shape) in shapes.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            text.Append(separator).Append(pass).Append(": ").Append(shape.Describe(frames));
            separator = ", ";
        }

        return text.ToString();
    }

    /// <summary>The settled samples of slots <paramref name="first"/> .. + <paramref name="count"/> into their passes' shapes.</summary>
    private static void AddSamples(Dictionary<string, Shape> shapes, IReadOnlyCollection<string> passes, Func<nint, string> name, int first, int count)
    {
        long now = Environment.TickCount64;

        for (int slot = first; slot < first + count; slot++)
        {
            var sample = Samples[slot];
            Samples[slot] = default;

            if (sample.Pipeline == 0 || now - sample.Recorded < SettleMilliseconds)
            {
                continue;
            }

            string pass = name(sample.Pipeline);

            if (passes.Contains(pass))
            {
                uint* record = _values + ((ulong)slot * SlotBytes / sizeof(uint));
                Of(shapes, pass).Add(sample.Kind, new ReadOnlySpan<uint>(record, RecordBytes / sizeof(uint)));
            }
        }
    }

    private static Shape Of(Dictionary<string, Shape> shapes, string pass)
    {
        if (!shapes.TryGetValue(pass, out var shape))
        {
            shape = new Shape();
            shapes[pass] = shape;
        }

        return shape;
    }

    /// <summary>One pipeline's work since the last report.</summary>
    private sealed class Tally
    {
        public long Dispatches;
        public long Groups;
        public long LargestGroups;
        public (uint X, uint Y, uint Z) Largest;
        public long Indirect;
        public uint MaxRecords;
        public long Frame = -1;
        public int Sampled;

        public void Clear()
        {
            Dispatches = 0;
            Groups = 0;
            LargestGroups = 0;
            Largest = default;
            Indirect = 0;
            MaxRecords = 0;
        }
    }

    /// <summary>A pass's work over the interval: its pipelines' tallies and its sampled first records.</summary>
    private sealed class Shape
    {
        private long _dispatches;
        private long _groups;
        private long _largestGroups;
        private (uint X, uint Y, uint Z) _largest;
        private long _indirect;
        private uint _maxRecords;
        private int _samples;
        private ulong _largestRecord;
        private string _record = string.Empty;

        public void Add(Tally tally)
        {
            _dispatches += tally.Dispatches;
            _groups += tally.Groups;
            _indirect += tally.Indirect;
            _maxRecords = Math.Max(_maxRecords, tally.MaxRecords);

            if (tally.LargestGroups >= _largestGroups)
            {
                _largestGroups = tally.LargestGroups;
                _largest = tally.Largest;
            }
        }

        /// <summary>A sampled first record: groups for a dispatch or mesh record, vertices (or indices) x instances for a draw.</summary>
        public void Add(int kind, ReadOnlySpan<uint> record)
        {
            var (first, second, third) = (record[0], record[1], record[^1]);
            ulong work = (ulong)first * second;
            string text = string.Create(CultureInfo.InvariantCulture, $"{first} x {second} (draw)");

            if (kind == IndirectClamp.DispatchKind || kind == IndirectClamp.MeshKind)
            {
                work *= third;
                text = string.Create(CultureInfo.InvariantCulture, $"{first} x {second} x {third} groups");
            }

            _samples++;

            if (work >= _largestRecord)
            {
                _largestRecord = work;
                _record = text;
            }
        }

        public string Describe(long frames)
        {
            var text = new StringBuilder();

            if (_dispatches > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $"{(double)_dispatches / frames:F1} dispatches, {(double)_groups / frames:F0} groups (largest {_largest.X} x {_largest.Y} x {_largest.Z})");
            }

            if (_indirect > 0)
            {
                string first = "no first record read";

                if (_samples > 0)
                {
                    first = $"largest first record {_record} of {_samples} sampled";
                }

                if (text.Length > 0)
                {
                    text.Append(" and ");
                }

                text.Append(CultureInfo.InvariantCulture, $"{(double)_indirect / frames:F1} indirect (up to {_maxRecords} records; {first})");
            }

            return text.ToString();
        }
    }

    /// <summary>A sampled indirect execution: its pipeline, signature kind and when the copy was recorded.</summary>
    private readonly record struct Sample(nint Pipeline, int Kind, long Recorded);
}
