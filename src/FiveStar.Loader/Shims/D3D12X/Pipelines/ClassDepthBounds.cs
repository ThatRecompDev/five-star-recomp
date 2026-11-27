// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Binding;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// The tiled lighting's class test as GREATER_EQUAL with a depth-bounds floor. VS_TiledLighting draws material class c (user SGPR 10) at
/// z = c * asfloat(0x37800080) with the depth test EQUAL against a depth that holds each pixel's class as that float
/// (the depth twin's copy). On the desktop that EQUAL failed for most pixels of the class although both read back as
/// the same float: the class-0 draw's z tests above 0.0 (w210, w211: GreaterEqual passes exactly the 0.0 pixels,
/// LessEqual none of them), so the lighting shaded slivers; a depth-bounds test half a step around the class, with the
/// test ALWAYS, failed in whole 16x16 blocks (w233, w234: the depth's coarse ranges still those of the depth before the
/// copy). GREATER_EQUAL tested every pixel exactly (w202). Such a pipeline (the vertex program's name, depth on, EQUAL,
/// no depth writes) gets GREATER_EQUAL, which keeps the classes up to its own, and the depth-bounds test, whose floor
/// half a step below its class (set before each of its draws, the game's bounds back after it) drops the lower ones; a
/// ceiling of 1.0 never rejects a block. On by default; <see cref="Enabled"/> false keeps the game's EQUAL.
/// </summary>
internal static unsafe class ClassDepthBounds
{
    public const string VertexProgram = "VS_TiledLighting";

    /// <summary>D3D12_DEPTH_STENCIL_DESC1.DepthBoundsTestEnable, D3D12_COMPARISON_FUNC_EQUAL and _GREATER_EQUAL (d3d12.h).</summary>
    private const int DepthBoundsField = 0x34;
    private const int DepthBoundsDescBytes = DepthBoundsField + sizeof(int);
    private const int Equal = 3;
    private const int GreaterEqual = 7;

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

    /// <summary>The depth-stencil desc of a class-test pipeline with GREATER_EQUAL and the bounds test on (a copy); else as it was.</summary>
    public static byte[] Apply(byte[] depthStencil, string names)
    {
        if (!Enabled || !IsClassTest(depthStencil, names))
        {
            return depthStencil;
        }

        byte[] changed = new byte[Math.Max(depthStencil.Length, DepthBoundsDescBytes)];
        depthStencil.CopyTo(changed, 0);
        BinaryPrimitives.WriteInt32LittleEndian(changed.AsSpan(DesktopStateBytes.DepthFuncField), GreaterEqual);
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
            && BinaryPrimitives.ReadInt32LittleEndian(depthStencil.AsSpan(DesktopStateBytes.DepthFuncField)) == GreaterEqual)
        {
            Marked[pipeline] = 0;
            Volatile.Write(ref _any, true);
        }
    }

    /// <summary>The bounds of class <paramref name="classIndex"/>: half a step below its float, up to 1.0.</summary>
    public static (float Min, float Max) Bounds(uint classIndex)
    {
        return ((classIndex * Step) - (Step * HalfStep), FullRangeMax);
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
