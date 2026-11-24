// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Shaders.Dxil.Container;

/// <summary>
/// The view-ID state of a graphics shader (<c>dx.viewIdState</c>, and the dependency tables of PSV0): the signature
/// scalar counts and which outputs each input scalar feeds. Like VIctory, the backend declares that every output
/// depends on every input instead of tracing the dependencies; drivers only use the tables to drop work.
/// </summary>
public static class ViewIdState
{
    private const int Components = 4;
    private const int BitsPerWord = 32;
    private const int Streams = 4;

    /// <summary>The scalars up to the last allocated component of <paramref name="elements"/>.</summary>
    public static int Scalars(IReadOnlyList<DxilSignatureElement> elements)
    {
        return elements.Where(element => element.IsAllocated)
            .Select(element => ((element.StartRow + element.Rows - 1) * Components) + element.StartColumn + element.Columns)
            .DefaultIfEmpty(0).Max();
    }

    /// <summary>The registers <paramref name="elements"/> use.</summary>
    public static int Vectors(IReadOnlyList<DxilSignatureElement> elements)
    {
        return elements.Where(element => element.IsAllocated).Select(element => element.StartRow + element.Rows).DefaultIfEmpty(0).Max();
    }

    /// <summary>The serialized state: input scalars, output scalars per stream, primitive scalars (mesh), the table.</summary>
    public static uint[] Serialize(ShaderDescription description)
    {
        var inputs = Scalars(description.Inputs);
        var outputs = Scalars(description.Outputs);
        var words = new List<uint> { (uint)inputs, (uint)outputs };

        if (description.Kind == DxilShaderKind.Geometry)
        {
            words.AddRange(Enumerable.Repeat(0u, Streams - 1));
        }

        if (description.Kind == DxilShaderKind.Mesh)
        {
            words.Add((uint)Scalars(description.Primitives));
        }

        var stride = (outputs + BitsPerWord - 1) / BitsPerWord;

        for (var input = 0; input < inputs; input++)
        {
            for (var word = 0; word < stride; word++)
            {
                words.Add(Dependencies(word, outputs));
            }
        }

        return [.. words];
    }

    /// <summary>The bits of dependency word <paramref name="word"/>: every output below <paramref name="outputs"/>.</summary>
    public static uint Dependencies(int word, int outputs)
    {
        var bits = Math.Clamp(outputs - (word * BitsPerWord), 0, BitsPerWord);

        return bits == BitsPerWord ? uint.MaxValue : (1u << bits) - 1;
    }
}
