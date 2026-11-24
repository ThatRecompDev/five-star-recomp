// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Shaders.Dxil.Container;

/// <summary>
/// Semantic names, their kinds and how each signature point treats them (the subset of DxilSigPoint.inl the
/// translator's stages use), and the interpolation modes dxc gives elements without a modifier.
/// </summary>
public static class Semantics
{
    /// <summary>DXIL interpolation modes.</summary>
    public const byte Constant = 1;
    public const byte Linear = 2;
    public const byte LinearCentroid = 3;
    public const byte LinearNoPerspective = 4;
    public const byte LinearNoPerspectiveCentroid = 5;
    public const byte LinearSample = 6;
    public const byte LinearNoPerspectiveSample = 7;

    private const string SystemPrefix = "SV_";

    private static readonly Dictionary<string, (string Name, SemanticKind Kind)> SystemValues = new[]
    {
        ("SV_VertexID", SemanticKind.VertexId), ("SV_InstanceID", SemanticKind.InstanceId), ("SV_Position", SemanticKind.Position),
        ("SV_RenderTargetArrayIndex", SemanticKind.RenderTargetArrayIndex), ("SV_ViewportArrayIndex", SemanticKind.ViewportArrayIndex),
        ("SV_ClipDistance", SemanticKind.ClipDistance), ("SV_CullDistance", SemanticKind.CullDistance), ("SV_PrimitiveID", SemanticKind.PrimitiveId),
        ("SV_GSInstanceID", SemanticKind.GsInstanceId), ("SV_SampleIndex", SemanticKind.SampleIndex), ("SV_IsFrontFace", SemanticKind.IsFrontFace),
        ("SV_Coverage", SemanticKind.Coverage), ("SV_InnerCoverage", SemanticKind.InnerCoverage), ("SV_Target", SemanticKind.Target),
        ("SV_Depth", SemanticKind.Depth), ("SV_DepthLessEqual", SemanticKind.DepthLessEqual), ("SV_DepthGreaterEqual", SemanticKind.DepthGreaterEqual),
        ("SV_StencilRef", SemanticKind.StencilRef), ("SV_DispatchThreadID", SemanticKind.DispatchThreadId), ("SV_GroupID", SemanticKind.GroupId),
        ("SV_GroupIndex", SemanticKind.GroupIndex), ("SV_GroupThreadID", SemanticKind.GroupThreadId), ("SV_ViewID", SemanticKind.ViewId),
        ("SV_Barycentrics", SemanticKind.Barycentrics), ("SV_ShadingRate", SemanticKind.ShadingRate), ("SV_CullPrimitive", SemanticKind.CullPrimitive),
    }.ToDictionary(entry => entry.Item1, entry => (entry.Item1, entry.Item2), StringComparer.OrdinalIgnoreCase);

    /// <summary>Splits a semantic into its name and index (<c>TEXCOORD11</c>: TEXCOORD, 11) and finds its kind.</summary>
    public static (string Name, int Index, SemanticKind Kind) Parse(string semantic)
    {
        var digits = semantic.Length;

        while (digits > 0 && char.IsAsciiDigit(semantic[digits - 1]))
        {
            digits--;
        }

        var name = semantic[..digits];
        var index = digits < semantic.Length ? int.Parse(semantic.AsSpan(digits), System.Globalization.CultureInfo.InvariantCulture) : 0;

        if (!name.StartsWith(SystemPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return (name, index, SemanticKind.Arbitrary);
        }

        if (!SystemValues.TryGetValue(name, out var system))
        {
            throw new DxilBackendException($"semantic {semantic} is not supported");
        }

        return (system.Name, index, system.Kind);
    }

    /// <summary>How <paramref name="point"/> treats <paramref name="kind"/>; a semantic it does not allow throws.</summary>
    public static SemanticInterpretation Interpret(SemanticKind kind, SignaturePoint point)
    {
        var result = kind switch
        {
            SemanticKind.Arbitrary => point == SignaturePoint.PixelOutput ? null : SemanticInterpretation.Arbitrary,
            SemanticKind.VertexId or SemanticKind.InstanceId => point == SignaturePoint.VertexInput ? SemanticInterpretation.SystemValue : null,
            SemanticKind.Position => Position(point),
            SemanticKind.RenderTargetArrayIndex or SemanticKind.ViewportArrayIndex or SemanticKind.ShadingRate => Layer(point),
            SemanticKind.ClipDistance or SemanticKind.CullDistance => Clip(point),
            SemanticKind.PrimitiveId => PrimitiveId(point),
            SemanticKind.IsFrontFace => point is SignaturePoint.PixelInput or SignaturePoint.GeometryOutput ? SemanticInterpretation.SystemGenerated : null,
            _ => Pixel(kind, point),
        };

        return result ?? throw new DxilBackendException($"semantic kind {kind} is not allowed at {point}");
    }

    /// <summary>Whether elements at <paramref name="point"/> carry an interpolation mode.</summary>
    public static bool HasInterpolation(SignaturePoint point)
    {
        return point is not (SignaturePoint.VertexInput or SignaturePoint.PixelOutput);
    }

    /// <summary>
    /// The interpolation mode of an element: from its modifiers, else linear for floats and constant for the rest; the
    /// position is never perspective-corrected.
    /// </summary>
    public static byte Interpolation(SignaturePoint point, SemanticKind kind, bool isFloat, IReadOnlySet<string> modifiers)
    {
        if (!HasInterpolation(point))
        {
            return 0;
        }

        byte mode;

        if (modifiers.Contains("nointerpolation") || point == SignaturePoint.MeshPrimitiveOutput)
        {
            mode = Constant;
        }
        else if (modifiers.Contains("sample"))
        {
            mode = modifiers.Contains("noperspective") ? LinearNoPerspectiveSample : LinearSample;
        }
        else if (modifiers.Contains("centroid"))
        {
            mode = modifiers.Contains("noperspective") ? LinearNoPerspectiveCentroid : LinearCentroid;
        }
        else if (modifiers.Contains("noperspective"))
        {
            mode = LinearNoPerspective;
        }
        else
        {
            mode = isFloat || modifiers.Contains("linear") ? Linear : Constant;
        }

        if (kind == SemanticKind.Position)
        {
            mode = mode switch
            {
                Linear => LinearNoPerspective,
                LinearCentroid => LinearNoPerspectiveCentroid,
                LinearSample => LinearNoPerspectiveSample,
                _ => mode,
            };
        }

        return mode;
    }

    private static SemanticInterpretation? Position(SignaturePoint point)
    {
        return point switch
        {
            SignaturePoint.VertexInput => SemanticInterpretation.Arbitrary,
            SignaturePoint.PixelOutput or SignaturePoint.MeshPrimitiveOutput => null,
            _ => SemanticInterpretation.SystemValue,
        };
    }

    private static SemanticInterpretation? Layer(SignaturePoint point)
    {
        return point is SignaturePoint.VertexOutput or SignaturePoint.PixelInput or SignaturePoint.GeometryVertexInput
            or SignaturePoint.GeometryOutput or SignaturePoint.MeshPrimitiveOutput ? SemanticInterpretation.SystemValue : null;
    }

    private static SemanticInterpretation? Clip(SignaturePoint point)
    {
        return point is SignaturePoint.VertexOutput or SignaturePoint.PixelInput or SignaturePoint.GeometryVertexInput
            or SignaturePoint.GeometryOutput or SignaturePoint.MeshVertexOutput ? SemanticInterpretation.ClipCull : null;
    }

    private static SemanticInterpretation? PrimitiveId(SignaturePoint point)
    {
        return point switch
        {
            SignaturePoint.PixelInput => SemanticInterpretation.SystemGenerated,
            SignaturePoint.GeometryOutput or SignaturePoint.MeshPrimitiveOutput => SemanticInterpretation.SystemValue,
            _ => null,
        };
    }

    private static SemanticInterpretation? Pixel(SemanticKind kind, SignaturePoint point)
    {
        if (point == SignaturePoint.PixelInput)
        {
            return kind switch
            {
                SemanticKind.SampleIndex => SemanticInterpretation.Shadow,
                SemanticKind.Coverage or SemanticKind.InnerCoverage => SemanticInterpretation.NotInSignature,
                SemanticKind.Barycentrics => SemanticInterpretation.NotPacked,
                _ => null,
            };
        }

        if (point == SignaturePoint.PixelOutput)
        {
            return kind switch
            {
                SemanticKind.Target => SemanticInterpretation.Target,
                SemanticKind.Depth or SemanticKind.DepthLessEqual or SemanticKind.DepthGreaterEqual or SemanticKind.StencilRef
                    or SemanticKind.Coverage => SemanticInterpretation.NotPacked,
                _ => null,
            };
        }

        return kind == SemanticKind.CullPrimitive && point == SignaturePoint.MeshPrimitiveOutput ? SemanticInterpretation.NotPacked : null;
    }
}
