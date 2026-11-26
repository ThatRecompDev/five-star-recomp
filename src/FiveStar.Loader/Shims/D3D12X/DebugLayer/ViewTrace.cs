// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FiveStar.Loader.Shims.D3D12X.Binding;
using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// <c>--trace-views &lt;pipeline texts&gt;</c>: before a draw, dispatch or ExecuteIndirect of a matching pipeline (the first
/// <see cref="TracesPerWindow"/> per pipeline every <see cref="WindowSeconds"/> s) each record of its bound tables is
/// logged with what it resolves to: a buffer record's console range, or the desktop view <see cref="ViewLiveness"/>
/// noted at its handle (shape, resource, size, format, alive or released, or a null view), with the record's first
/// dwords (the console descriptor). CPU only: nothing is added to the game's lists, unlike <see cref="NanScan"/>.
/// For the exposure histogram reading black (w170). Off by default.
/// </summary>
internal static unsafe class ViewTrace
{
    private const int TracesPerWindow = 2;
    private const double WindowSeconds = 30;

    /// <summary>Record dwords shown: the console descriptor's first 8 (a 32-byte T#, or a V# in its first 4).</summary>
    private const int RecordDwords = 8;

    private const int RecordsDword = 2;
    private const int StrideDword = 1;
    private const uint HighMask = 0xFFFF;
    private const int HighShift = 32;

    private static readonly ConcurrentDictionary<nint, bool> Matching = new();
    private static readonly ConcurrentDictionary<nint, (long Start, int Count)> Windows = new();

    /// <summary>Texts (comma-separated) one of which the traced pipelines' names hold; null when off.</summary>
    public static string? Filter { get; set; }

    public static bool Enabled => Filter is not null;

    /// <summary>Before work of <paramref name="list"/>: its tables' records logged when its pipeline matches and its window has room.</summary>
    public static void Before(XboxObject* list, bool compute)
    {
        nint pipeline = ListActivity.Of(list)->Pipeline;

        if (pipeline == 0 || !Matching.GetOrAdd(pipeline, static key => NanScan.Matches(QueueProgress.PipelineName(key), Filter!)) || !Admit(pipeline))
        {
            return;
        }

        var bindings = CommandListBindings.Of(list);
        var layout = compute ? bindings->ComputeLayout : bindings->GraphicsLayout;
        var views = ShimContext.Runtime.D3D12X.DescriptorHeaps.Views;

        if (layout == null || views is null)
        {
            return;
        }

        uint* data = compute ? bindings->Compute : bindings->Graphics;
        uint first = compute ? UserData.ComputeBase : UserData.GraphicsBase;
        var text = new StringBuilder();

        for (uint parameter = 0; parameter < (uint)layout->Count; parameter++)
        {
            uint sgpr = first + layout->Offsets[parameter];

            if (sgpr < CommandListBindings.UserDataDwords && layout->Kind(parameter) == RootParameterKind.Table)
            {
                Table(text, views, parameter, data[sgpr], layout->Extents[parameter]);
            }
        }

        string work = compute ? "dispatch" : "draw";
        ShimContext.Runtime.Log.Info($"  [view trace] {QueueProgress.PipelineName(pipeline)} ({work}, present {ShimContext.Runtime.D3D12X.Presenter.Presents}):{text}");
    }

    /// <summary>The first <see cref="TracesPerWindow"/> of a pipeline in each window.</summary>
    private static bool Admit(nint pipeline)
    {
        long now = Stopwatch.GetTimestamp();
        var window = Windows.GetOrAdd(pipeline, (now, 0));

        if (Stopwatch.GetElapsedTime(window.Start, now).TotalSeconds > WindowSeconds)
        {
            window = (now, 0);
        }

        if (window.Count >= TracesPerWindow)
        {
            return false;
        }

        Windows[pipeline] = (window.Start, window.Count + 1);

        return true;
    }

    private static void Table(StringBuilder text, ShaderVisibleHeap views, uint parameter, uint offset, uint extent)
    {
        if (offset >= DescriptorWindow.NullTableBase || offset % DescriptorWindow.ViewRecordBytes != 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n      table {parameter}: window 0x{offset:x}, outside the view records");

            return;
        }

        uint index = (offset - DescriptorWindow.ViewBase) / DescriptorWindow.ViewRecordBytes;

        for (uint i = 0; i < extent && index + i < views.Count; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n      table {parameter} entry {i} (record {index + i}): {Resolve(views, index + i)}; dwords {Dwords(views, index + i)}");
        }
    }

    /// <summary>What a record resolves to: a buffer record's console range, or the noted desktop view.</summary>
    private static string Resolve(ShaderVisibleHeap views, uint index)
    {
        uint high = views.RecordDword(index, StrideDword) & HighMask;

        if (high != 0 && views.RecordDword(index, RecordsDword) != 0)
        {
            ulong address = views.RecordDword(index, 0) | ((ulong)high << HighShift);

            return string.Create(CultureInfo.InvariantCulture, $"buffer at 0x{address:x}, {views.RecordDword(index, RecordsDword)} records");
        }

        var noted = ViewLiveness.Peek(views.Source(index));

        if (noted is null)
        {
            return "no view noted at its handle";
        }

        var (view, live) = noted.Value;

        if (view.Shape == ViewShape.Null || view.Resource == 0)
        {
            return "null view (reads 0)";
        }

        if (!live)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{view.Shape} of released resource 0x{view.Resource:x}");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{view.Shape} of 0x{view.Resource:x} {Describe(view.Resource)}");
    }

    /// <summary>A live desktop resource's size, mips and format (GetDesc only).</summary>
    private static string Describe(nint resource)
    {
        Marshal.AddRef(resource);
        using var texture = new ID3D12Resource(resource);
        var description = texture.Description;

        return string.Create(CultureInfo.InvariantCulture, $"{description.Width}x{description.Height}x{description.DepthOrArraySize} mips {description.MipLevels} format {(int)description.Format}");
    }

    private static string Dwords(ShaderVisibleHeap views, uint index)
    {
        var dwords = new string[RecordDwords];

        for (int dword = 0; dword < RecordDwords; dword++)
        {
            dwords[dword] = views.RecordDword(index, dword).ToString("x8", CultureInfo.InvariantCulture);
        }

        return string.Join(' ', dwords);
    }
}
