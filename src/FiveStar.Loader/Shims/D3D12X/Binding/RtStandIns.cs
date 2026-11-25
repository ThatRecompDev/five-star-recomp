// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Present;
using FiveStar.Loader.Shims.D3D12X.States;

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// Stand-in outputs for the ray-tracing passes held without <c>--raytracing</c> (default on; <c>--no-rt-stand-ins</c>
/// turns them off): when such a pass's dispatch is skipped, the texture UAVs of its compute tables get a constant
/// instead of what a frame ago or nothing left there (<see cref="UavFill"/>). Ray-traced shadow masks read fully lit,
/// planar reflections black; the screen-probe light, whose rays are held, is overwritten after
/// CS_ScreenProbes_TemporalResolve runs (or at its skip) with a modest grey, as VIctory's ambient stand-in does
/// (ZJOU-VICTORY 1fc58f3 after Belobug 4f45ab8; vitutu1 c3faf2a adds shadow-mask and exposure stand-ins; facts only).
/// Raw-buffer writers (the screen-probe rays, the deferred gather) are left alone.
/// </summary>
internal static unsafe class RtStandIns
{
    /// <summary>ID3D12Device::GetDescriptorHandleIncrementSize and the CBV_SRV_UAV heap type (d3d12.h).</summary>
    private const int IncrementSlot = 15;
    private const int ViewHeapType = 0;

    /// <summary>DXGI_FORMAT_R32_UINT.</summary>
    private const int R32Uint = 42;

    /// <summary>
    /// DXGI R32G32B32A32_FLOAT, R16G16B16A16_FLOAT and R11G11B10_FLOAT: the formats a colour-only rule fills. The
    /// screen-probe resolve also writes probe metadata through its tables (octahedral normals as x * 0.5 + 0.5, packed
    /// bit fields), which a grey would corrupt.
    /// </summary>
    private static readonly int[] ColourFormats = [2, 10, 26];

    private static readonly (string Name, FillValue Value, bool AfterRun, bool ColourOnly, string Stands)[] Rules =
    [
        ("shadowmask~CS_TraceShadowRays", new FillValue(1, 1, 1, uint.MaxValue), false, false, "ray-traced shadow mask written fully lit"),
        ("rtdirectionalshadows~", new FillValue(1, 1, 1, uint.MaxValue), false, false, "ray-traced directional shadows written fully lit"),
        ("CS_PPR_Raytrace", new FillValue(0, 0, 0, 0), false, false, "planar reflections written black"),
        ("CS_ScreenProbes_TemporalResolve", FillValue.Shared(AmbientLevel), true, true, "screen-probe light overwritten with grey 0.6 after it runs"),
    ];

    /// <summary>The desktop pipelines whose outputs are overwritten after they run, with their rule (<see cref="Named"/>).</summary>
    private static readonly ConcurrentDictionary<nint, int> AfterRunPipelines = new();
    private static readonly long[] FilledByRule = new long[Rules.Length];
    private static long _filled;

    /// <summary>The grey of the screen-probe light stand-in (VIctory's 0.6).</summary>
    public const float AmbientLevel = 0.6f;

    /// <summary>Whether held passes get stand-in outputs (default on; off with <c>--no-rt-stand-ins</c>).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>UAVs filled so far.</summary>
    public static long Filled => Interlocked.Read(ref _filled);

    /// <summary>The sample line: UAVs each rule filled so far, or that none was.</summary>
    public static string Summary
    {
        get
        {
            var used = Enumerable.Range(0, Rules.Length).Where(rule => Interlocked.Read(ref FilledByRule[rule]) != 0).ToList();

            if (used.Count == 0)
            {
                return "ray-tracing stand-ins: none filled";
            }

            return "ray-tracing stand-ins (approximate): " + string.Join(", ", used.Select(rule => $"{Rules[rule].Name} {Interlocked.Read(ref FilledByRule[rule])} UAVs"));
        }
    }

    /// <summary>The rule for a pipeline name, or -1.</summary>
    public static int RuleOf(string name)
    {
        for (int rule = 0; rule < Rules.Length; rule++)
        {
            if (name.Contains(Rules[rule].Name, StringComparison.Ordinal))
            {
                return rule;
            }
        }

        return -1;
    }

    /// <summary>A desktop pipeline was built from <paramref name="names"/>: an after-run rule is kept for it (a reused address loses an old one).</summary>
    public static void Named(nint pipeline, string names)
    {
        int rule = RuleOf(names);

        if (Enabled && rule >= 0 && Rules[rule].AfterRun)
        {
            AfterRunPipelines[pipeline] = rule;

            return;
        }

        AfterRunPipelines.TryRemove(pipeline, out _);
    }

    /// <summary>A dispatch of a held pipeline named <paramref name="name"/> was skipped: its outputs get the rule's value.</summary>
    public static void AtSkip(XboxObject* list, string name)
    {
        int rule = RuleOf(name);

        if (rule >= 0)
        {
            Fill(list, rule, name);
        }
    }

    /// <summary>After a dispatch that ran: a pass whose rule writes after it (the screen-probe light) gets its outputs overwritten.</summary>
    [HotPath]
    public static void AfterDispatch(XboxObject* list, nint pipeline)
    {
        if (!AfterRunPipelines.IsEmpty && AfterRunPipelines.TryGetValue(pipeline, out int rule))
        {
            Fill(list, rule, QueueProgress.PipelineName(pipeline));
        }
    }

    /// <summary>
    /// Writes rule <paramref name="rule"/>'s value over the texture UAVs of the list's compute tables, then sets the
    /// list's compute state again. The first fill of each rule is logged with the pipeline <paramref name="name"/>.
    /// </summary>
    private static void Fill(XboxObject* list, int rule, string name)
    {
        var runtime = ShimContext.Runtime.D3D12X;
        var bindings = CommandListBindings.Of(list);
        var layout = bindings->ComputeLayout;
        var window = runtime.Bindings.Window;

        // Without the game's compute root signature set there is nothing to restore after the fill's own.
        if (bindings->ComputeRoot == 0 || layout == null || layout->UavRanges == 0 || window.Data == null || window.ViewHeap == 0)
        {
            return;
        }

        nint inner = list->Inner;
        int filled = 0;

        for (int range = 0; range < layout->UavRanges; range++)
        {
            uint pointer = bindings->Compute[UserData.ComputeBase + layout->Offsets[layout->UavParameters[range]]];
            uint end = (uint)(layout->UavStarts[range] + layout->UavCounts[range]);
            runtime.DescriptorHeaps.MirrorTable(pointer, end);
            filled += FillRecords(inner, window, pointer, layout->UavStarts[range], end, rule, filled == 0);
        }

        if (filled == 0)
        {
            return;
        }

        Barrier(inner);
        Interlocked.Add(ref _filled, filled);

        if (Interlocked.Add(ref FilledByRule[rule], filled) == filled)
        {
            ShimContext.Log?.Warn($"[d3d12_xs] ray-tracing stand-in (approximate, not the pass's result; --no-rt-stand-ins turns it off): {name}: {Rules[rule].Stands}");
        }

        CommandListBindingMethods.RestoreCompute(list);
    }

    /// <summary>Fills the texture UAVs of records <paramref name="first"/> to <paramref name="end"/> of the table at window offset <paramref name="pointer"/>; how many.</summary>
    private static int FillRecords(nint list, DescriptorWindow window, uint pointer, uint first, uint end, int rule, bool barrierFirst)
    {
        var value = Rules[rule].Value;
        nint device = ShimContext.Runtime.D3D12X.Objects.DesktopDevice;
        nint cpuStart = DesktopCalls.HeapCpuStart(window.ViewHeap);
        ulong gpuStart = DesktopCalls.HeapGpuStart(window.ViewHeap);
        uint increment = ((delegate* unmanaged<nint, int, uint>)ComObject.Slot(device, IncrementSlot))(device, ViewHeapType);
        int filled = 0;

        for (uint record = first; record < end; record++)
        {
            ulong offset = pointer + ((ulong)record * DescriptorWindow.ViewRecordBytes);

            if (offset + DescriptorWindow.ViewRecordBytes > DescriptorWindow.NullTableBase)
            {
                break;
            }

            uint index = ((uint*)(window.Data + offset))[DescriptorWindow.MirroredDesktopDword];

            if (!UavViews.TryFind(cpuStart + (nint)((ulong)index * increment), out var view) || !Takes(view, Rules[rule].ColourOnly))
            {
                continue;
            }

            if (barrierFirst && filled == 0)
            {
                Barrier(list);
            }

            if (UavFill.Record(device, list, gpuStart, index, view, value))
            {
                filled++;
            }
        }

        return filled;
    }

    /// <summary>
    /// Whether a rule writes <paramref name="view"/>: never an integer view (tile lists, counters, packed fields) but,
    /// for a colour-only rule, an R32_UINT view of a texture the game also reads as R9G9B9E5 (its shared-exponent
    /// light, packed by <see cref="UavFill"/>); for a colour-only rule otherwise a float colour format alone.
    /// </summary>
    public static bool Takes(UavView view, bool colourOnly)
    {
        if (UavFill.Integer(view.Format))
        {
            return colourOnly && view.Format == R32Uint && UavViews.SharedExponent(view.Resource);
        }

        return !colourOnly || Array.IndexOf(ColourFormats, view.Format) >= 0;
    }

    /// <summary>A UAV barrier on every resource: the fill after the pass's writes, the readers after the fill.</summary>
    private static void Barrier(nint list)
    {
        var barrier = new TransitionBarrier { Type = TransitionBarrier.UavType, Resource = 0 };
        BarrierWriter.Write(list, &barrier);
    }
}
