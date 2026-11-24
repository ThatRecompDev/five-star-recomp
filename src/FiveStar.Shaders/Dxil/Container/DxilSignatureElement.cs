// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Shaders.Dxil.Container;

/// <summary>
/// One element of a DXIL signature, as the entry metadata, ISG1/OSG1/PSG1 and PSV0 describe it. The packer sets the
/// start row and column (-1: no register); the lowering records the components the program reads or writes.
/// </summary>
public sealed class DxilSignatureElement
{
    /// <summary>DXIL component types (DxilConstants.h ComponentType).</summary>
    public const byte Bool = 1;
    public const byte Int32 = 4;
    public const byte Uint32 = 5;
    public const byte Float32 = 9;

    public required int Id { get; init; }

    /// <summary>The semantic without its index (system values in their canonical spelling).</summary>
    public required string Name { get; init; }

    public required int Index { get; init; }

    public required SemanticKind Kind { get; init; }

    public required SemanticInterpretation Interpretation { get; init; }

    public required byte ComponentType { get; init; }

    /// <summary>DXIL interpolation mode (0 where the signature point has none).</summary>
    public required byte Interpolation { get; init; }

    public required int Columns { get; init; }

    public int Rows { get; init; } = 1;

    public int StartRow { get; set; } = -1;

    public int StartColumn { get; set; } = -1;

    /// <summary>Components read (inputs) or written (outputs), from bit 0 = the element's first column.</summary>
    public byte Usage { get; set; }

    public bool IsAllocated => StartRow >= 0;

    /// <summary>The element's columns as a register mask (from column 0 when it has no register).</summary>
    public byte Mask => (byte)(((1 << Columns) - 1) << Math.Max(StartColumn, 0));

    /// <summary>D3D_REGISTER_COMPONENT_TYPE: 1 uint, 2 int, 3 float.</summary>
    public byte RegisterComponentType => ComponentType switch
    {
        Int32 => 2,
        Float32 => 3,
        _ => 1,
    };

    /// <summary>D3D_NAME of the semantic (0 for arbitrary ones).</summary>
    public uint SystemValue => Kind switch
    {
        SemanticKind.Position => 1,
        SemanticKind.ClipDistance => 2,
        SemanticKind.CullDistance => 3,
        SemanticKind.RenderTargetArrayIndex => 4,
        SemanticKind.ViewportArrayIndex => 5,
        SemanticKind.VertexId => 6,
        SemanticKind.PrimitiveId => 7,
        SemanticKind.InstanceId => 8,
        SemanticKind.IsFrontFace => 9,
        SemanticKind.SampleIndex => 10,
        SemanticKind.Barycentrics => 23,
        SemanticKind.ShadingRate => 24,
        SemanticKind.CullPrimitive => 25,
        SemanticKind.Target => 64,
        SemanticKind.Depth => 65,
        SemanticKind.Coverage => 66,
        SemanticKind.DepthGreaterEqual => 67,
        SemanticKind.DepthLessEqual => 68,
        SemanticKind.StencilRef => 69,
        SemanticKind.InnerCoverage => 70,
        _ => 0,
    };

    /// <summary>Whether the element is listed in the signature (everything but values read through their own operation).</summary>
    public bool InSignature => Interpretation != SemanticInterpretation.NotInSignature;

    public override string ToString()
    {
        return $"{Name}{Index} r{StartRow} c{StartColumn} x{Columns}";
    }
}
