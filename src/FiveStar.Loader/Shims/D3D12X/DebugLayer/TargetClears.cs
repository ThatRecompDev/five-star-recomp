// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Text;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// Diagnostics for an image that accumulates over frames (a target the game relies on being cleared keeps old
/// contents). <c>--target-clear-census</c>: every <see cref="CensusInterval"/> presents, the render targets bound in the
/// frame before, how many the game cleared (ClearRenderTargetView) and the bound ones it never cleared, with their
/// shape. <c>--clear-targets-each-frame</c>: each render target is cleared to 0 where it is first bound in a frame (its
/// previous frame's contents gone, as if the console had cleared it); a diagnostic that also wipes real history.
/// Render-target views are known by the desktop handle they were written to.
/// </summary>
internal static unsafe class TargetClears
{
    /// <summary>Presents between two census lines.</summary>
    public const long CensusInterval = 300;

    /// <summary>Never-cleared targets named per census line.</summary>
    private const int NamedTargets = 24;

    /// <summary>ID3D12Device::GetDescriptorHandleIncrementSize, D3D12_DESCRIPTOR_HEAP_TYPE_RTV, ID3D12GraphicsCommandList::ClearRenderTargetView.</summary>
    private const int IncrementSlot = 15;
    private const int RenderTargetHeapType = 2;
    private const int DesktopClearRenderTarget = 48;

    private static readonly ConcurrentDictionary<nint, (nint Resource, string Shape)> Views = new();
    private static readonly ConcurrentDictionary<nint, long> ClearedAt = new();
    private static readonly ConcurrentDictionary<nint, (long Frame, int Binds)> BoundAt = new();
    private static readonly ConcurrentDictionary<nint, long> GameCleared = new();
    private static readonly ConcurrentDictionary<(ulong New, ulong Old), byte> Ranges = new();
    private static readonly ConcurrentDictionary<nint, nint[]> Pending = new();
    private static readonly ConcurrentDictionary<nint, string> Writers = new();
    private static readonly float[] Zero = [0, 0, 0, 0];
    private static long _logged = -1;
    private static uint _increment;

    /// <summary>Census lines (<c>--target-clear-census</c>).</summary>
    public static bool Census { get; set; }

    /// <summary>Each render target cleared at its first bind in a frame (<c>--clear-targets-each-frame</c>).</summary>
    public static bool ClearEachFrame { get; set; }

    public static bool Any => Census || ClearEachFrame;

    /// <summary>A render-target view of the desktop <paramref name="resource"/> was written at <paramref name="handle"/>.</summary>
    public static void NoteView(nint handle, nint resource, string shape)
    {
        Views[handle] = (resource, shape);
    }

    /// <summary>ClearRenderTargetView of the game at <paramref name="handle"/>.</summary>
    public static void GameClear(nint handle)
    {
        if (Views.TryGetValue(handle, out var view))
        {
            GameCleared[view.Resource] = Frame();
        }
    }

    /// <summary>
    /// The game bound <paramref name="count"/> targets (one handle to a range when <paramref name="single"/>) on the
    /// desktop <paramref name="list"/>, after they were moved to RENDER_TARGET: noted, and cleared at the first bind of
    /// the frame with <see cref="ClearEachFrame"/>.
    /// </summary>
    public static void Bound(nint list, uint count, nint* handles, bool single)
    {
        long frame = Frame();
        Report(frame);
        var resources = new List<nint>();

        for (uint i = 0; i < count && handles != null; i++)
        {
            nint handle = Handle(handles, i, single);

            if (!Views.TryGetValue(handle, out var view))
            {
                continue;
            }

            var bound = BoundAt.GetValueOrDefault(view.Resource);
            int binds = 1;

            if (bound.Frame == frame)
            {
                binds = bound.Binds + 1;
            }

            BoundAt[view.Resource] = (frame, binds);
            resources.Add(view.Resource);

            if (ClearEachFrame && ClearedAt.GetValueOrDefault(view.Resource, -1) != frame)
            {
                ClearedAt[view.Resource] = frame;

                fixed (float* zero = Zero)
                {
                    ((delegate* unmanaged<nint, nint, float*, uint, int*, void>)ComObject.Slot(list, DesktopClearRenderTarget))(list, handle, zero, 0, null);
                }
            }
        }

        if (Census && resources.Count > 0)
        {
            Pending[list] = [.. resources];
        }
    }

    /// <summary>
    /// A draw on the desktop <paramref name="list"/> with <paramref name="pipeline"/> (a stand-in or a desktop pipeline):
    /// the targets the list bound last are named by the first pipeline that draws into them, for the census.
    /// </summary>
    public static void Drew(nint list, nint pipeline)
    {
        if (!Pending.TryRemove(list, out var resources))
        {
            return;
        }

        string name = PipelineName(pipeline);

        foreach (nint resource in resources)
        {
            Writers.TryAdd(resource, name);
        }
    }

    private static string PipelineName(nint pipeline)
    {
        string name = ShimContext.Runtime.D3D12X.Pipelines?.StandIns.Name(pipeline) ?? "unnamed";

        if (name == "unnamed")
        {
            name = QueueProgress.PipelineName(pipeline);
        }

        return name;
    }

    /// <summary>
    /// A GPU-written texture made over memory another GPU-written texture held (the game places two targets in one
    /// memory, as a transient pool or a history pair can): on the console they share the bytes, here they are two
    /// textures, so what one is written with never shows through the other. Each range is logged once.
    /// </summary>
    public static void Replaced(Resources.GpuTexture old, Resources.GpuTexture replacement)
    {
        if (!Ranges.TryAdd((replacement.Address, old.Address), 0))
        {
            return;
        }

        var (oldShape, newShape) = (old.Console, replacement.Console);
        ShimContext.Log?.Info($"[clears] GPU-written texture at 0x{replacement.Address:x} ({newShape.Width}x{newShape.Height} format {newShape.Format}) made over one at 0x{old.Address:x} ({oldShape.Width}x{oldShape.Height} format {oldShape.Format}): on the console they share memory, here writes to either stay in its own texture ({Ranges.Count} such ranges)");
    }

    /// <summary>The present count frames are told apart by (the presenter's; a test sets its own).</summary>
    internal static Func<long> FrameSource { get; set; } = () => ShimContext.Runtime.D3D12X.Presenter.Presents;

    private static long Frame()
    {
        return FrameSource();
    }

    /// <summary>Once per <see cref="CensusInterval"/> presents: the census of the frame before.</summary>
    private static void Report(long frame)
    {
        long previous = frame - 1;

        if (!Census || previous < 0 || previous % CensusInterval != 0 || Interlocked.Exchange(ref _logged, previous) == previous)
        {
            return;
        }

        var shapes = Views.Values.GroupBy(view => view.Resource).ToDictionary(group => group.Key, group => group.First().Shape);
        var bound = BoundAt.Where(entry => entry.Value.Frame == previous).ToList();
        var cleared = bound.Where(entry => GameCleared.GetValueOrDefault(entry.Key, -1) == previous).ToList();
        var never = bound.Where(entry => !GameCleared.ContainsKey(entry.Key)).OrderByDescending(entry => entry.Value.Binds).ToList();
        var text = new StringBuilder($"[clears] frame {previous}: {bound.Count} render targets bound, {cleared.Count} cleared by the game in that frame, {never.Count} never cleared since the boot began");

        foreach (var entry in never.Take(NamedTargets))
        {
            text.Append($"\n[clears]     never cleared: 0x{entry.Key:x} {shapes.GetValueOrDefault(entry.Key, "?")}, bound {entry.Value.Binds} times, first drawn by {Writers.GetValueOrDefault(entry.Key, "no draw seen")}");
        }

        ShimContext.Log?.Info(text.ToString());
    }

    private static nint Handle(nint* handles, uint index, bool single)
    {
        if (!single)
        {
            return handles[index];
        }

        if (_increment == 0)
        {
            nint device = ShimContext.Runtime.D3D12X.Objects.DesktopDevice;
            _increment = ((delegate* unmanaged<nint, int, uint>)ComObject.Slot(device, IncrementSlot))(device, RenderTargetHeapType);
        }

        return handles[0] + (nint)(index * _increment);
    }
}
