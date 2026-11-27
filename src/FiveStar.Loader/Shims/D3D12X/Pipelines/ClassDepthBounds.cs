// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Binding;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// The tiled lighting's class test as a depth-bounds test. VS_TiledLighting draws material class c (user SGPR 10) at
/// z = c * asfloat(0x37800080) with the depth test EQUAL against a depth that holds each pixel's class as that float
/// (the depth twin's copy). On the desktop that EQUAL failed for most pixels of the class although both read back as
/// the same float: the class-0 draw's z tests above 0.0 (w210, w211: GreaterEqual passes exactly the 0.0 pixels,
/// LessEqual none of them), so the lighting shaded slivers. Such a pipeline (the vertex program's name, depth on,
/// EQUAL, no depth writes) gets the test ALWAYS and the depth-bounds test on; before each of its draws the bounds are
/// set half a step around its class's float, which the stored depth alone is compared with, and the game's bounds go
/// back after it. On by default; <see cref="Enabled"/> false keeps the game's EQUAL.
/// </summary>
internal static unsafe class ClassDepthBounds
{
    public const string VertexProgram = "VS_TiledLighting";

    /// <summary>D3D12_DEPTH_STENCIL_DESC1.DepthBoundsTestEnable, D3D12_COMPARISON_FUNC_EQUAL and _ALWAYS (d3d12.h).</summary>
    private const int DepthBoundsField = 0x34;
    private const int DepthBoundsDescBytes = DepthBoundsField + sizeof(int);
    private const int Equal = 3;
    private const int Always = 8;

    /// <summary>The user SGPR the vertex program reads its class from.</summary>
    private const int ClassSgpr = 10;

    /// <summary>ID3D12GraphicsCommandList1::OMSetDepthBounds (d3d12.h).</summary>
    private const int SetDepthBoundsSlot = 62;

    private const float FullRangeMax = 1f;
    private const float HalfStep = 0.5f;

    /// <summary>asfloat(0x37800080), the float per class step; the bounds lie half a step either side.</summary>
    private static readonly float Step = BitConverter.UInt32BitsToSingle(0x37800080);

    private static readonly ConcurrentDictionary<nint, byte> Marked = new();
    private static bool _any;

    /// <summary>Whether such pipelines are rewritten (<c>--no-class-depth-bounds</c> turns it off).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>The depth-stencil desc of a class-test pipeline with ALWAYS and the bounds test on (a copy); else as it was.</summary>
    public static byte[] Apply(byte[] depthStencil, string names)
    {
        if (!Enabled || !IsClassTest(depthStencil, names))
        {
            return depthStencil;
        }

        byte[] changed = new byte[Math.Max(depthStencil.Length, DepthBoundsDescBytes)];
        depthStencil.CopyTo(changed, 0);
        BinaryPrimitives.WriteInt32LittleEndian(changed.AsSpan(DesktopStateBytes.DepthFuncField), Always);
        BinaryPrimitives.WriteInt32LittleEndian(changed.AsSpan(DepthBoundsField), 1);

        return changed;
    }

    /// <summary>Whether a desc is the class test of <see cref="VertexProgram"/>: depth on, no writes, EQUAL.</summary>
    public static bool IsClassTest(byte[] depthStencil, string names)
    {
        return names.Contains(VertexProgram, StringComparison.Ordinal)
            && BinaryPrimitives.ReadInt32LittleEndian(depthStencil.AsSpan(DesktopStateBytes.DepthEnableField)) != 0
            && BinaryPrimitives.ReadInt32LittleEndian(depthStencil.AsSpan(DesktopStateBytes.DepthWriteMaskField)) == 0
            && BinaryPrimitives.ReadInt32LittleEndian(depthStencil.AsSpan(DesktopStateBytes.DepthFuncField)) == Equal;
    }

    /// <summary>Notes a built pipeline whose desc <see cref="Apply"/> rewrote.</summary>
    public static void Built(nint pipeline, byte[] depthStencil)
    {
        if (depthStencil.Length >= DepthBoundsDescBytes && BinaryPrimitives.ReadInt32LittleEndian(depthStencil.AsSpan(DepthBoundsField)) != 0
            && BinaryPrimitives.ReadInt32LittleEndian(depthStencil.AsSpan(DesktopStateBytes.DepthFuncField)) == Always)
        {
            Marked[pipeline] = 0;
            Volatile.Write(ref _any, true);
        }
    }

    /// <summary>The bounds of class <paramref name="classIndex"/>: half a step either side of its float.</summary>
    public static (float Min, float Max) Bounds(uint classIndex)
    {
        float depth = classIndex * Step;

        return (depth - (Step * HalfStep), depth + (Step * HalfStep));
    }

    /// <summary>Before a draw on <paramref name="list"/>: a class-test pipeline gets its class's bounds; true when set.</summary>
    [HotPath]
    public static bool Before(XboxObject* list, nint pipeline)
    {
        if (!Volatile.Read(ref _any) || !Marked.ContainsKey(pipeline))
        {
            return false;
        }

        var (min, max) = Bounds(CommandListBindings.Of(list)->Graphics[ClassSgpr]);
        SetBounds(list->Inner, min, max);

        return true;
    }

    /// <summary>After such a draw: the game's own bounds again (the full range when it set none).</summary>
    [HotPath]
    public static void After(XboxObject* list)
    {
        var state = ListReplayState.Of(list);

        if ((state->Set & ListReplayState.DepthBoundsBit) != 0)
        {
            SetBounds(list->Inner, state->DepthMin, state->DepthMax);

            return;
        }

        SetBounds(list->Inner, 0, FullRangeMax);
    }

    [HotPath]
    private static void SetBounds(nint list, float min, float max)
    {
        ((delegate* unmanaged<nint, float, float, void>)ComObject.Slot(list, SetDepthBoundsSlot))(list, min, max);
    }
}
