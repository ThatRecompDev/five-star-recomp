// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Translation.Core;

namespace FiveStar.Shaders.Translation.Emitters;

/// <summary>
/// Address fixes D3D needs where RDNA does not: unnormalized (texel) coordinates (the unorm modifier) are divided by the
/// level-0 size, and a sample's texel offsets outside D3D's -8..7 (RDNA's reach -32..31) keep their in-range part as
/// the offset argument while the rest moves into the coordinates as offset / size. Gathers take -32..31 offsets in D3D
/// and keep theirs. Cubes are left as they are.
/// </summary>
internal static class TexelAddress
{
    private const int MinimumOffset = -8;
    private const int MaximumOffset = 7;

    /// <summary>The request with its coordinates fixed; <paramref name="foldOffsets"/> for a sample, not a gather.</summary>
    public static ImageRequest Fix(TranslationState state, ImageRequest request, bool foldOffsets)
    {
        bool fold = foldOffsets && request.Offsets is not null;

        if ((!request.Unnormalized && !fold) || request.IsCube || request.Dimension is not ("1D" or "1D_ARRAY" or "2D" or "2D_ARRAY" or "3D"))
        {
            return request;
        }

        var size = Size(state, request);

        if (request.Unnormalized)
        {
            request = request with { CoordinateScale = Padded(request, $"(1.0 / {size})", "1.0") };
        }

        if (fold)
        {
            request = Folded(state, request, size);
        }

        return request;
    }

    private static ImageRequest Folded(TranslationState state, ImageRequest request, string size)
    {
        var spatial = request.SpatialComponents;
        var offset = $"_ofs{request.Number}";
        var type = Vector("int", spatial);
        state.Emit($"  {type} {offset} = {request.Offsets}; {type} {offset}c = clamp({offset}, {MinimumOffset}, {MaximumOffset});");

        var shift = Padded(request, $"({Vector("float", spatial)}({offset} - {offset}c) / {size})", "0.0");

        return request with { Offsets = $"{offset}c", CoordinateShift = shift };
    }

    /// <summary>Emits the level-0 size query and returns the spatial size as a float vector.</summary>
    private static string Size(TranslationState state, ImageRequest request)
    {
        var n = request.Number;
        var (outputs, size) = request.Dimension switch
        {
            "1D" => ($"_ow{n}, _onl{n}", $"float(_ow{n})"),
            "1D_ARRAY" => ($"_ow{n}, _oe{n}, _onl{n}", $"float(_ow{n})"),
            "2D" => ($"_ow{n}, _oh{n}, _onl{n}", $"float2(_ow{n}, _oh{n})"),
            "2D_ARRAY" => ($"_ow{n}, _oh{n}, _oe{n}, _onl{n}", $"float2(_ow{n}, _oh{n})"),
            _ => ($"_ow{n}, _oh{n}, _od{n}, _onl{n}", $"float3(_ow{n}, _oh{n}, _od{n})"),
        };

        state.Emit($"  uint {outputs}; {request.Texture}.GetDimensions(0u, {outputs});");

        return size;
    }

    /// <summary>A spatial vector widened to all address components, the slice taking <paramref name="fill"/>.</summary>
    private static string Padded(ImageRequest request, string spatial, string fill)
    {
        if (request.Components == request.SpatialComponents)
        {
            return spatial;
        }

        return $"float{request.Components}({spatial}, {fill})";
    }

    private static string Vector(string scalar, int components)
    {
        if (components == 1)
        {
            return scalar;
        }

        return $"{scalar}{components}";
    }
}
