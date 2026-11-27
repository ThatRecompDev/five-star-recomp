// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// <c>--depth-func-override text=Func[;text=Func...]</c> (debug): pipelines whose programs' names hold the text get that
/// depth comparison (Never, Less, Equal, LessEqual, Greater, NotEqual, GreaterEqual, Always; D3D12_COMPARISON_FUNC 1-8),
/// and the stencil test off, so a pass whose pixels fail its depth or stencil test (TL_PS draws each material class
/// with EQUAL) shows whether that test is what culls them. The first rule whose text matches wins.
/// </summary>
internal static class DepthFuncOverride
{
    private static readonly string[] Functions = ["Never", "Less", "Equal", "LessEqual", "Greater", "NotEqual", "GreaterEqual", "Always"];

    /// <summary>The rules, text and D3D12_COMPARISON_FUNC; empty for none.</summary>
    public static IReadOnlyList<(string Text, int Function)> Rules { get; set; } = [];

    /// <summary>"text=Func;..." as rules; null when a part is not one.</summary>
    public static IReadOnlyList<(string Text, int Function)>? Parse(string value)
    {
        var rules = new List<(string Text, int Function)>();

        foreach (string part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = part.LastIndexOf('=');
            int function = equals <= 0 ? -1 : Array.FindIndex(Functions, name => name.Equals(part[(equals + 1)..], StringComparison.OrdinalIgnoreCase));

            if (function < 0)
            {
                return null;
            }

            rules.Add((part[..equals], function + 1));
        }

        if (rules.Count == 0)
        {
            return null;
        }

        return rules;
    }

    /// <summary>The depth-stencil desc with the comparison of the first rule <paramref name="names"/> matches and stencil off (a copy), else as it was.</summary>
    public static byte[] Apply(byte[] depthStencil, string names)
    {
        foreach (var (text, function) in Rules)
        {
            if (!names.Contains(text, StringComparison.Ordinal))
            {
                continue;
            }

            byte[] changed = [.. depthStencil];
            BinaryPrimitives.WriteInt32LittleEndian(changed.AsSpan(DesktopStateBytes.DepthFuncField), function);
            BinaryPrimitives.WriteInt32LittleEndian(changed.AsSpan(DesktopStateBytes.StencilEnableField), 0);

            return changed;
        }

        return depthStencil;
    }
}
