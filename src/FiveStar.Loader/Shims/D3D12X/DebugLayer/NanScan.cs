// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Binding;
using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// <c>--d3d12-nan-scan &lt;pipeline text&gt;</c>: the inputs of a matching draw, dispatch or ExecuteIndirect (the first
/// <see cref="ScansPerWindow"/> per pipeline every <see cref="WindowSeconds"/> s) are read as the work will see them and
/// searched for NaN and Inf (<see cref="FloatScan"/>), for the HDR lighting's NaN red. Every record of the bound tables
/// within the root signature's extents: a texture SRV is copied into a readback buffer in the game's list right before
/// the work (its state on that list, as <see cref="DrawProbes"/> does), a buffer record's or CBV record's console range
/// (in game memory the GPU reads in place) and each root CBV are read from memory once the list's submission finished.
/// Each input with NaN or Inf is logged with its table, record, resource, format, count and first value, and one line
/// counts the inputs scanned clean. The views come from <see cref="ViewLiveness"/>'s notes. Off by default.
/// </summary>
internal static unsafe class NanScan
{
    private const int ScansPerWindow = 3;

    /// <summary>--d3d12-nan-scan-dwords: offset and count.</summary>
    private const int DwordsParts = 2;
    private const string HexPrefix = "0x";
    private const double WindowSeconds = 30;
    private const int PollMilliseconds = 200;
    private const double GiveUpSeconds = 30;

    /// <summary>Bytes read of a buffer range or a root CBV (whose size the root does not say).</summary>
    private const int BufferBytes = 1 << 20;
    private const int RootConstantBytes = 4096;

    /// <summary>Readback buffers alive at once, as <see cref="DrawProbes.LiveBytesCap"/>.</summary>
    private const long LiveBytesCap = DrawProbes.LiveBytesCap;

    private const int RecordsDword = 2;
    private const int StrideDword = 1;
    private const int StrideShift = 16;
    private const uint StrideMask = 0x3FFF;
    private const uint HighMask = 0xFFFF;
    private const int HighShift = 32;

    /// <summary>ID3D12Fence::GetCompletedValue (d3d12.h).</summary>
    private const int CompletedSlot = 8;

    private static readonly ConcurrentDictionary<nint, (long Start, int Count)> Windows = new();
    private static readonly ConcurrentDictionary<nint, bool> Matching = new();
    private static readonly ConcurrentDictionary<nint, long> Resets = new();
    private static readonly List<Scan> Pending = [];
    private static readonly List<Scan> Reported = [];
    private static readonly object Gate = new();
    private static long _liveBytes;
    private static int _polling;

    /// <summary>Textures of at most this many texels have their values shown with <see cref="ShowValues"/> (1x1, 5x1).</summary>
    private const int SmallTexels = 8;

    /// <summary>Channel values shown of one small texture.</summary>
    private const int ShownChannels = SmallTexels * 4;

    /// <summary>Texts (comma-separated) one of which the scanned pipelines' names contain; null when off.</summary>
    public static string? Filter { get; set; }

    public static bool Enabled => Filter is not null;

    /// <summary>
    /// <c>--trace-exposure</c> (<see cref="ExposureTrace"/>): small float textures show their values even when clean, and
    /// records holding a null view or one of a released texture are named.
    /// </summary>
    public static bool ShowValues { get; set; }

    /// <summary>Whether a pipeline name holds one of the texts of <see cref="Filter"/>.</summary>
    public static bool Matches(string name, string filter)
    {
        return filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary><c>--d3d12-nan-scan-dwords &lt;offset&gt;:&lt;count&gt;</c>: the floats of every memory input shown from that byte offset; null for none.</summary>
    public static (int Offset, int Count)? Dwords { get; set; }

    /// <summary>"0x1a0:16" (or decimal) as (byte offset, dwords).</summary>
    public static (int Offset, int Count) ParseDwords(string text)
    {
        var parts = text.Split(':');

        if (parts.Length != DwordsParts)
        {
            throw new ArgumentException($"--d3d12-nan-scan-dwords takes <offset>:<count>, not '{text}'");
        }

        return (Number(parts[0]), Number(parts[1]));
    }

    private static int Number(string text)
    {
        if (text.StartsWith(HexPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return int.Parse(text.AsSpan(HexPrefix.Length), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return int.Parse(text, CultureInfo.InvariantCulture);
    }

    /// <summary>A list's Reset: readback buffers of its earlier recordings can go once reported.</summary>
    [HotPath]
    public static void Reset(nint list)
    {
        if (Enabled)
        {
            CountReset(list);
        }
    }

    private static void CountReset(nint list)
    {
        Resets.AddOrUpdate(list, 1, static (_, count) => count + 1);
    }

    /// <summary>Before a draw, dispatch or ExecuteIndirect of <paramref name="list"/>: scans it when its pipeline matches and its window has room.</summary>
    public static void Before(XboxObject* list, bool compute)
    {
        nint pipeline = ListActivity.Of(list)->Pipeline;

        if (pipeline == 0 || !Matching.GetOrAdd(pipeline, static key => Matches(QueueProgress.PipelineName(key), Filter!)) || !Admit(pipeline))
        {
            return;
        }

        var scan = new Scan((nint)list, QueueProgress.PipelineName(pipeline), ListSubmission.Of((nint)list)->Value, Resets.GetValueOrDefault((nint)list), Stopwatch.GetTimestamp());
        Collect(list, compute, scan);

        lock (Gate)
        {
            Pending.Add(scan);
        }

        if (Interlocked.Exchange(ref _polling, 1) == 0)
        {
            new Thread(Poll) { IsBackground = true, Name = "fivestar nan scan" }.Start();
        }
    }

    /// <summary>The first <see cref="ScansPerWindow"/> of a pipeline in each window.</summary>
    private static bool Admit(nint pipeline)
    {
        long now = Stopwatch.GetTimestamp();
        var window = Windows.GetOrAdd(pipeline, (now, 0));

        if (Stopwatch.GetElapsedTime(window.Start, now).TotalSeconds > WindowSeconds)
        {
            window = (now, 0);
        }

        if (window.Count >= ScansPerWindow)
        {
            return false;
        }

        Windows[pipeline] = (window.Start, window.Count + 1);

        return true;
    }

    private static void Collect(XboxObject* list, bool compute, Scan scan)
    {
        var bindings = CommandListBindings.Of(list);
        var layout = compute ? bindings->ComputeLayout : bindings->GraphicsLayout;
        var views = ShimContext.Runtime.D3D12X.DescriptorHeaps.Views;

        if (layout == null || views is null)
        {
            return;
        }

        uint* data = compute ? bindings->Compute : bindings->Graphics;
        uint first = compute ? UserData.ComputeBase : UserData.GraphicsBase;

        for (uint parameter = 0; parameter < (uint)layout->Count; parameter++)
        {
            uint sgpr = first + layout->Offsets[parameter];

            if (sgpr + 1 >= CommandListBindings.UserDataDwords)
            {
                continue;
            }

            var kind = layout->Kind(parameter);

            if (kind == RootParameterKind.Table)
            {
                Table(list, scan, views, parameter, data[sgpr], layout->Extents[parameter]);
            }
            else if (kind == RootParameterKind.ConstantBuffer)
            {
                ulong address = data[sgpr] | ((ulong)data[sgpr + 1] << HighShift);
                scan.Memory.Add(new MemoryInput($"root CBV {parameter}", address, RootConstantBytes));
            }
        }
    }

    private static void Table(XboxObject* list, Scan scan, ShaderVisibleHeap views, uint parameter, uint offset, uint extent)
    {
        if (offset >= DescriptorWindow.NullTableBase || offset % DescriptorWindow.ViewRecordBytes != 0)
        {
            return;
        }

        uint index = (offset - DescriptorWindow.ViewBase) / DescriptorWindow.ViewRecordBytes;

        for (uint i = 0; i < extent && index + i < views.Count; i++)
        {
            string label = string.Create(CultureInfo.InvariantCulture, $"table {parameter} (window 0x{offset:x}) entry {i}, record {index + i}");
            uint high = views.RecordDword(index + i, StrideDword) & HighMask;

            // A buffer or CBV record with a console base: its range in game memory.
            if (high != 0 && views.RecordDword(index + i, RecordsDword) != 0)
            {
                ulong address = views.RecordDword(index + i, 0) | ((ulong)high << HighShift);
                scan.Memory.Add(new MemoryInput(label, address, RangeBytes(views, index + i)));

                continue;
            }

            var view = ViewLiveness.Peek(views.Source(index + i));

            if (view is not null && view.Value.View.Shape == ViewShape.TextureSrv && view.Value.Live)
            {
                CopyTexture(list, scan, label, view.Value.View);
            }
            else if (ShowValues && view is not null)
            {
                NoteView(scan, label, view.Value.View, view.Value.Live);
            }
        }
    }

    /// <summary>With <see cref="ShowValues"/>: a null view (the game's, or one a texture release nulled) or a view of a released texture.</summary>
    private static void NoteView(Scan scan, string label, SlotView view, bool live)
    {
        if (view.Shape == ViewShape.Null)
        {
            scan.Notes.Add($"{label}: null view (reads 0)");
        }
        else if (!live)
        {
            scan.Notes.Add(string.Create(CultureInfo.InvariantCulture, $"{label}: {view.Shape} of released texture 0x{view.Resource:x}"));
        }
    }

    /// <summary>A buffer record's bytes: num_records strides (bytes for a raw view), at most <see cref="BufferBytes"/>.</summary>
    private static int RangeBytes(ShaderVisibleHeap views, uint index)
    {
        ulong records = views.RecordDword(index, RecordsDword);
        ulong stride = (views.RecordDword(index, StrideDword) >> StrideShift) & StrideMask;

        if (stride != 0)
        {
            records *= stride;
        }

        return (int)Math.Min(records, BufferBytes);
    }

    /// <summary>Copies the texture's first subresource into a readback buffer in the game's list, from its state there.</summary>
    private static void CopyTexture(XboxObject* list, Scan scan, string label, SlotView view)
    {
        var d3d12x = ShimContext.Runtime.D3D12X;
        int state = d3d12x.ListStates.Find(list)?.Current(view.Resource, view.Home) ?? view.Home;
        var copy = Copy(d3d12x.Objects.DesktopDevice, list->Inner, view.Resource, state, label);

        if (copy is not null)
        {
            scan.Textures.Add(copy);
        }
    }

    /// <summary>The copy of <paramref name="texture"/>'s first subresource recorded on <paramref name="list"/>; null for one it skips (block-compressed, not 2D, over the cap).</summary>
    public static TextureCopy? Copy(nint device, nint list, nint texture, int state, string label)
    {
        Marshal.AddRef(texture);
        using var resource = new ID3D12Resource(texture);
        var description = resource.Description;

        if (description.Dimension != ResourceDimension.Texture2D || description.SampleDescription.Count > 1 || FloatScan.Layout((int)description.Format).Channels == 0)
        {
            return null;
        }

        Marshal.AddRef(device);
        using var desktop = new ID3D12Device(device);
        var footprints = new PlacedSubresourceFootPrint[1];
        var rows = new uint[1];
        var rowBytes = new ulong[1];
        desktop.GetCopyableFootprints(description, 0, 1, 0, footprints, rows, rowBytes, out ulong total);

        if (Interlocked.Add(ref _liveBytes, (long)total) > LiveBytesCap)
        {
            Interlocked.Add(ref _liveBytes, -(long)total);

            return null;
        }

        var buffer = desktop.CreateCommittedResource(new Vortice.Direct3D12.HeapProperties(HeapType.Readback), HeapFlags.None, Vortice.Direct3D12.ResourceDescription.Buffer(total), ResourceStates.CopyDest);
        Marshal.AddRef(list);
        using var commands = new ID3D12GraphicsCommandList(list);
        var resting = (ResourceStates)state;

        commands.ResourceBarrierTransition(resource, resting, ResourceStates.CopySource);
        commands.CopyTextureRegion(new Vortice.Direct3D12.TextureCopyLocation(buffer, footprints[0]), 0, 0, 0, new Vortice.Direct3D12.TextureCopyLocation(resource, 0), null);
        commands.ResourceBarrierTransition(resource, ResourceStates.CopySource, resting);

        return new TextureCopy(label, texture, buffer, (long)total, (int)footprints[0].Footprint.RowPitch, (int)description.Width, (int)rows[0], (int)description.Format);
    }

    /// <summary>The non-finite values the copy holds (the GPU finished the list); null for a format not scanned.</summary>
    public static FloatScanResult? Read(TextureCopy copy)
    {
        void* data;
        copy.Buffer.Map(0, null, &data).CheckError();

        try
        {
            return FloatScan.Texture(new ReadOnlySpan<byte>(data, checked((int)copy.Bytes)), copy.RowPitch, copy.Width, copy.Rows, copy.Format);
        }
        finally
        {
            copy.Buffer.Unmap(0);
        }
    }

    private static void Poll()
    {
        while (true)
        {
            Thread.Sleep(PollMilliseconds);

            List<Scan> done;

            lock (Gate)
            {
                done = Pending.Where(Finished).ToList();
                Pending.RemoveAll(done.Contains);
                Release();
            }

            foreach (var scan in done)
            {
                Report(scan);
            }
        }
    }

    /// <summary>Whether the list was submitted after the scan and that submission finished; one waiting too long is dropped.</summary>
    private static bool Finished(Scan scan)
    {
        var submission = ListSubmission.Of(scan.List);

        if (Stopwatch.GetElapsedTime(scan.At).TotalSeconds > GiveUpSeconds)
        {
            return true;
        }

        if (submission->Fence == 0 || submission->Value <= scan.Submitted)
        {
            return false;
        }

        return ((delegate* unmanaged<nint, ulong>)ComObject.Slot(submission->Fence, CompletedSlot))(submission->Fence) >= submission->Value;
    }

    /// <summary>Readback buffers of reported scans whose list was reset since (no execution can write them any more).</summary>
    private static void Release()
    {
        for (int i = Reported.Count - 1; i >= 0; i--)
        {
            var scan = Reported[i];

            if (Resets.GetValueOrDefault(scan.List) == scan.Resets)
            {
                continue;
            }

            foreach (var copy in scan.Textures)
            {
                copy.Buffer.Dispose();
                Interlocked.Add(ref _liveBytes, -copy.Bytes);
            }

            Reported.RemoveAt(i);
        }
    }

    private static void Report(Scan scan)
    {
        var log = ShimContext.Runtime.Log;
        var findings = new StringBuilder();
        int clean = ReportTextures(scan, findings);

        foreach (var note in scan.Notes)
        {
            findings.Append(CultureInfo.InvariantCulture, $"\n      {note}");
        }

        foreach (var input in scan.Memory)
        {
            var bytes = ReadMemory(input);
            var result = FloatScan.Dwords(bytes);
            string values = Values(bytes);

            if (result.Count == 0)
            {
                clean++;

                if (values.Length != 0)
                {
                    findings.Append(CultureInfo.InvariantCulture, $"\n      {input.Label}: memory 0x{input.Address:x}, clean{values}");
                }

                continue;
            }

            findings.Append(CultureInfo.InvariantCulture, $"\n      {input.Label}: memory 0x{input.Address:x}, {input.Bytes} bytes as float32: {result} (X = byte offset){values}");
        }

        lock (Gate)
        {
            Reported.Add(scan);
        }

        log.Info($"  [nan scan] {scan.Pipeline}: {clean} inputs scanned clean, {scan.Textures.Count + scan.Memory.Count} in all (block-compressed and non-float textures are not scanned){findings}");
    }

    /// <summary>The textures holding NaN or Inf (and small ones' values with <see cref="ShowValues"/>) appended; the count of clean ones.</summary>
    private static int ReportTextures(Scan scan, StringBuilder findings)
    {
        int clean = 0;

        foreach (var copy in scan.Textures)
        {
            var result = Read(copy);
            string shape = string.Create(CultureInfo.InvariantCulture, $"{copy.Label}: texture 0x{copy.Resource:x} format {copy.Format} {copy.Width}x{copy.Rows}");

            if (result is null || result.Value.Count == 0)
            {
                clean++;

                if (ShowValues && copy.Width * copy.Rows <= SmallTexels)
                {
                    findings.Append(CultureInfo.InvariantCulture, $"\n      {shape}, clean: {string.Join(", ", Texels(copy).Select(value => value.ToString("G6", CultureInfo.InvariantCulture)))}");
                }

                continue;
            }

            findings.Append(CultureInfo.InvariantCulture, $"\n      {shape}: {result.Value}");
        }

        return clean;
    }

    /// <summary>The channel values of a small copied texture (the GPU finished the list).</summary>
    private static List<float> Texels(TextureCopy copy)
    {
        void* data;
        copy.Buffer.Map(0, null, &data).CheckError();

        try
        {
            return FloatScan.Values(new ReadOnlySpan<byte>(data, checked((int)copy.Bytes)), copy.RowPitch, copy.Width, copy.Rows, copy.Format, ShownChannels);
        }
        finally
        {
            copy.Buffer.Unmap(0);
        }
    }

    /// <summary>Game memory at a console address, as far as it reads (through the kernel: an unmapped range reads as nothing).</summary>
    private static byte[] ReadMemory(MemoryInput input)
    {
        var bytes = new byte[input.Bytes];
        nuint done = 0;

        fixed (byte* to = bytes)
        {
            _ = Kernel32.ReadProcessMemory(Kernel32.GetCurrentProcess(), (nint)input.Address, to, (nuint)bytes.Length, &done);
        }

        return bytes.AsSpan(0, (int)done).ToArray();
    }

    /// <summary>
    /// "; floats at +0x1a0: 1, 0, ..." for <see cref="Dwords"/> (<c>--d3d12-nan-scan-dwords</c>: the rows a program reads,
    /// such as PS_VelocityFill's previous view-projection at +0x1a0); empty when off or past the input.
    /// </summary>
    public static string Values(ReadOnlySpan<byte> bytes)
    {
        var dwords = Dwords;

        if (dwords is null || dwords.Value.Offset >= bytes.Length)
        {
            return string.Empty;
        }

        var shown = new List<string>();

        for (int at = dwords.Value.Offset; at + sizeof(float) <= bytes.Length && shown.Count < dwords.Value.Count; at += sizeof(float))
        {
            shown.Add(BitConverter.ToSingle(bytes.Slice(at, sizeof(float))).ToString("G6", CultureInfo.InvariantCulture));
        }

        return string.Create(CultureInfo.InvariantCulture, $"; floats at +0x{dwords.Value.Offset:x}: {string.Join(", ", shown)}");
    }

    /// <summary>A copied texture: its label, desktop resource, readback buffer and footprint.</summary>
    public sealed record TextureCopy(string Label, nint Resource, ID3D12Resource Buffer, long Bytes, int RowPitch, int Width, int Rows, int Format);

    /// <summary>A range of game memory to read once the work ran.</summary>
    private sealed record MemoryInput(string Label, ulong Address, int Bytes);

    /// <summary>One scanned draw: its list, pipeline, the list's last submission value and reset count then, and its inputs.</summary>
    private sealed record Scan(nint List, string Pipeline, ulong Submitted, long Resets, long At)
    {
        public List<TextureCopy> Textures { get; } = [];

        public List<MemoryInput> Memory { get; } = [];

        /// <summary>Null and released views (<see cref="ShowValues"/>).</summary>
        public List<string> Notes { get; } = [];
    }
}
