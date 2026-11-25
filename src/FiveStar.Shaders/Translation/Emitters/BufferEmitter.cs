// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;
using FiveStar.Shaders.Translation.Core;

namespace FiveStar.Shaders.Translation.Emitters;

/// <summary>
/// MUBUF/MTBUF buffer loads and stores on the V#'s view (<see cref="BufferAddress.View"/>). Raw dword accesses use
/// BufLoad/BufStore (a V# the program also writes is loaded through its RW view). Formatted accesses decode/encode in
/// the prelude's FmtLoad/FmtStore with a format code (layout * 8 + numeric type, GCN dfmt/nfmt numbering) from the
/// instruction (tbuffer) or from the hardware format in V# dword3, and loads apply dword3's destination swizzle.
/// </summary>
internal sealed partial class BufferEmitter
{
    // MUBUF operand order: vdata, vaddr, srsrc, soffset.
    private const int Data = 0;
    private const int Address = 1;
    private const int Resource = 2;
    private const int SOffset = 3;
    private const int FormatDword = 3;
    private const int BytesPerDword = 4;
    private const int Vec4 = 4;
    private const int LayoutShift = 8;

    private static readonly IReadOnlyDictionary<string, int> DwordCounts = new Dictionary<string, int>
    {
        ["dword"] = 1,
        ["dwordx2"] = 2,
        ["dwordx3"] = 3,
        ["dwordx4"] = 4,
    };

    private static readonly IReadOnlyDictionary<string, int> ComponentCounts = new Dictionary<string, int>
    {
        ["x"] = 1,
        ["xy"] = 2,
        ["xyz"] = 3,
        ["xyzw"] = 4,
    };

    // Data format layouts (GCN BUF_DATA_FORMAT numbering).
    private static readonly IReadOnlyDictionary<string, int> Layouts = new Dictionary<string, int>
    {
        ["8"] = 1,
        ["16"] = 2,
        ["8_8"] = 3,
        ["32"] = 4,
        ["16_16"] = 5,
        ["10_11_11"] = 6,
        ["11_11_10"] = 7,
        ["10_10_10_2"] = 8,
        ["2_10_10_10"] = 9,
        ["8_8_8_8"] = 10,
        ["32_32"] = 11,
        ["16_16_16_16"] = 12,
        ["32_32_32"] = 13,
        ["32_32_32_32"] = 14,
    };

    // Numeric formats (GCN BUF_NUM_FORMAT numbering).
    private static readonly IReadOnlyDictionary<string, int> NumericTypes = new Dictionary<string, int>
    {
        ["UNORM"] = 0,
        ["SNORM"] = 1,
        ["USCALED"] = 2,
        ["SSCALED"] = 3,
        ["UINT"] = 4,
        ["SINT"] = 5,
        ["FLOAT"] = 7,
    };

    private readonly TranslationState _state;
    private readonly OperandAccess _operands;
    private HashSet<int>? _writtenResources;

    public BufferEmitter(TranslationState state, OperandAccess operands)
    {
        _state = state;
        _operands = operands;
    }

    /// <summary>buffer_load_dword* / buffer_store_dword*.</summary>
    public void Translate(string op, IReadOnlyList<Operand> a, InstructionModifiers modifiers)
    {
        if (!DwordCounts.TryGetValue(op[(op.LastIndexOf('_') + 1)..], out var count))
        {
            throw new UnsupportedProgramException(op);
        }

        var resource = a[Resource];
        var address = BufferAddress.Build(_operands, a[Address], resource, a[SOffset], modifiers);
        var range = BufferAddress.Range(_operands, a[Address], resource, a[SOffset], modifiers);

        if (op.StartsWith("buffer_load", StringComparison.Ordinal))
        {
            var writable = resource.Kind == OperandKind.Scalar && WrittenResources().Contains(resource.Low);
            var view = BufferAddress.View(_operands, resource, writable, address);
            var function = writable ? "BufLoadRW" : "BufLoad";

            for (var dword = 0; dword < count; dword++)
            {
                var inRange = BufferAddress.InRange(range, dword * BytesPerDword);
                _operands.Write(a[Data], $"select({inRange}, {function}({view}.x, {view}.y + {dword * BytesPerDword}u), 0u)", dword);
            }

            return;
        }

        var target = BufferAddress.View(_operands, resource, true, address);

        for (var dword = 0; dword < count; dword++)
        {
            var inRange = BufferAddress.InRange(range, dword * BytesPerDword);
            _state.Emit($"  if (active && {inRange}) {{ TRACE_VSTORE({_operands.Read(resource, 0)}, {_operands.Read(resource, 1)}, {address} + {dword * BytesPerDword}u, {_operands.Read(a[Data], dword)}); BufStore({target}.x, {target}.y + {dword * BytesPerDword}u, {_operands.Read(a[Data], dword)}); }}");
        }
    }

    /// <summary>buffer_load/store_format_* and tbuffer_load/store_format_*.</summary>
    public void TranslateFormatted(string op, IReadOnlyList<Operand> a, InstructionModifiers modifiers)
    {
        if (!ComponentCounts.TryGetValue(op[(op.LastIndexOf('_') + 1)..], out var count))
        {
            throw new UnsupportedProgramException(op);
        }

        var resource = a[Resource];
        var dword3 = _operands.Read(resource, FormatDword);
        var code = op.StartsWith("tbuffer", StringComparison.Ordinal) ? InstructionFormat(modifiers) : $"buf_code({dword3})";
        var store = op.Contains("store", StringComparison.Ordinal);

        var address = BufferAddress.Build(_operands, a[Address], resource, a[SOffset], modifiers);
        var format = _operands.SnapOne(code);
        var range = BufferAddress.Range(_operands, a[Address], resource, a[SOffset], modifiers);
        var inRange = BufferAddress.InRange((range.Offset, $"fmt_limit({range.Limit}, {format})"), 0);
        var view = BufferAddress.View(_operands, resource, store, address);
        var byteAddress = $"{view}.y";
        var swizzle = _operands.SnapOne(dword3);

        if (store)
        {
            var data = new List<string>();

            for (var component = 0; component < Vec4; component++)
            {
                var value = component < count ? _operands.Read(a[Data], component) : "0u";
                data.Add(value);
            }

            _state.Emit($"  if (active && {inRange}) FmtStore({view}.x, {byteAddress}, {format}, uint4({string.Join(", ", data)}), {count}u);");

            return;
        }

        var name = $"_fmt{_state.NextTemporary()}";
        _state.Emit($"  uint4 {name} = select({inRange}, fmt_swizzle(FmtLoad({view}.x, {byteAddress}, {format}), {swizzle}, {format}), uint4(0u, 0u, 0u, 0u));");

        for (var component = 0; component < count; component++)
        {
            _operands.Write(a[Data], $"{name}.{"xyzw"[component]}", component);
        }
    }

    private static string InstructionFormat(InstructionModifiers modifiers)
    {
        var format = FormatPattern().Match(modifiers.Value("format") ?? string.Empty);

        if (!format.Success || !Layouts.TryGetValue(format.Groups[1].Value, out var layout))
        {
            throw new UnsupportedProgramException($"tbuffer format {modifiers.Value("format")}");
        }

        return $"{(layout * LayoutShift) + NumericTypes[format.Groups["numeric"].Value]}u";
    }

    /// <summary>SGPR bases of the V#s this program writes through (buffer stores and atomics), from every instruction.</summary>
    private HashSet<int> WrittenResources()
    {
        if (_writtenResources is not null)
        {
            return _writtenResources;
        }

        _writtenResources = [];

        foreach (var instruction in _state.Instructions)
        {
            var match = WritePattern().Match(instruction.Text);

            if (!match.Success)
            {
                continue;
            }

            var arguments = ArgumentList.Split(InstructionModifiers.Strip(" " + match.Groups[1].Value));
            var register = arguments.Count > Resource ? OperandParser.TryParseRegister(arguments[Resource]) : null;

            if (register is not null && register.Kind == OperandKind.Scalar)
            {
                _writtenResources.Add(register.Low);
            }
        }

        return _writtenResources;
    }

    [GeneratedRegex(@"^\[BUF_FMT_(\w+?)_(?<numeric>UNORM|SNORM|USCALED|SSCALED|UINT|SINT|FLOAT)\]")]
    private static partial Regex FormatPattern();

    [GeneratedRegex(@"^(?:buffer_store\w*|buffer_atomic\w*|tbuffer_store\w*)\s+(.*)")]
    private static partial Regex WritePattern();
}
