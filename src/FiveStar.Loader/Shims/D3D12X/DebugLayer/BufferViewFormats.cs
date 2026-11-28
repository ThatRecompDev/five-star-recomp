// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// What a buffer record's dword 3 says of the data (<see cref="Binding.BufferRecords"/>, <see cref="Binding.BufferFormatCodes"/>),
/// for <see cref="NanScan"/>: OOB_SELECT (bits 28-29) tells a raw view (3) and a structured one (0) from a typed or
/// constant one (1), and only a typed one's unified format (bits 12-18) names its data. A raw or structured view's
/// dwords may be floats or integers; a typed view of an integer or normalized format holds no floats at all (issue
/// #64: a 640 KiB TAA2 input of 0xffffffff counted as 163840 NaN).
/// </summary>
internal static class BufferViewFormats
{
    private const int FormatShift = 12;
    private const uint FormatMask = 0x7F;
    private const int BoundsShift = 28;
    private const uint BoundsMask = 0x3;
    private const uint RawBounds = 3;
    private const uint StructuredBounds = 0;

    // DXGI_FORMAT (dxgiformat.h) of the unified float formats: R32G32B32A32, R32G32B32, R16G16B16A16, R32G32, R11G11B10, R16G16, R32, R16.
    private const int Rgba32Float = 2;
    private const int Rgb32Float = 6;
    private const int Rgba16Float = 10;
    private const int Rg32Float = 16;
    private const int R11G11B10Float = 26;
    private const int Rg16Float = 34;
    private const int R32Float = 41;
    private const int R16Float = 54;

    // The unified float formats (RDNA 2 ISA, buffer data formats).
    private const uint U16Float = 13;
    private const uint U32Float = 22;
    private const uint U16x2Float = 29;
    private const uint U10x11x11Float = 36;
    private const uint U32x2Float = 64;
    private const uint U16x4Float = 71;
    private const uint U32x3Float = 74;
    private const uint U32x4Float = 77;

    /// <summary>What the record's dword 3 says.</summary>
    public enum Form
    {
        /// <summary>A typed or constant view of a float format: scanned in that format.</summary>
        Float,

        /// <summary>A typed view of an integer or normalized format (or none): not scanned.</summary>
        NotFloat,

        /// <summary>A raw view: scanned as float32, but its dwords may be integers.</summary>
        Raw,

        /// <summary>A structured view: as a raw one.</summary>
        Structured,
    }

    /// <summary>The form of a record's data, its unified format and, for a float one, the DXGI format it is scanned in.</summary>
    public static (Form Form, uint Unified, int Format) Of(uint dword3)
    {
        uint unified = (dword3 >> FormatShift) & FormatMask;

        switch ((dword3 >> BoundsShift) & BoundsMask)
        {
            case RawBounds:
                return (Form.Raw, unified, R32Float);
            case StructuredBounds:
                return (Form.Structured, unified, R32Float);
        }

        int format = Float(unified);

        if (format == 0)
        {
            return (Form.NotFloat, unified, 0);
        }

        return (Form.Float, unified, format);
    }

    /// <summary>The DXGI float format of a unified buffer format (RDNA 2 ISA, buffer data formats); 0 for one that is not float.</summary>
    private static int Float(uint unified)
    {
        return unified switch
        {
            U16Float => R16Float,
            U32Float => R32Float,
            U16x2Float => Rg16Float,
            U10x11x11Float => R11G11B10Float,
            U32x2Float => Rg32Float,
            U16x4Float => Rgba16Float,
            U32x3Float => Rgb32Float,
            U32x4Float => Rgba32Float,
            _ => 0,
        };
    }
}
