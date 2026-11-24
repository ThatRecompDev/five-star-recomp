// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace FiveStar.Shaders.Dxil.Container;

/// <summary>
/// PSV0 (pipeline state validation) with runtime info version 2, as validator 1.6 and 1.7 write it: runtime info, the
/// bound resources, the string and semantic index tables, the signature elements, and the input-to-output dependency
/// table of the view-ID state.
/// </summary>
public static class PsvWriter
{
    private const int RuntimeInfoBytes = 48;
    private const int BindingBytes = 24;
    private const int ElementBytes = 16;
    private const int MinimumWaveOffset = 16;
    private const int MaximumWaveOffset = 20;
    private const int StageOffset = 24;
    private const int UnionOffset = 26;
    private const int InputElementsOffset = 28;
    private const int ThreadsOffset = 36;
    private const int MaxVerticesOffset = 12;
    private const int Components = 4;
    private const int BitsPerWord = 32;
    private const byte Allocated = 0x40;
    private const int ColumnShift = 4;

    public static byte[] Write(ShaderDescription description, IReadOnlyList<PsvBinding> bindings)
    {
        var output = new List<byte>();
        AppendWords(output, RuntimeInfoBytes);
        output.AddRange(RuntimeInfo(description));
        AppendWords(output, (uint)bindings.Count);

        if (bindings.Count > 0)
        {
            AppendWords(output, BindingBytes);

            foreach (var binding in bindings)
            {
                AppendWords(output, binding.Type, binding.Space, binding.Register, binding.Register, binding.Kind, 0);
            }
        }

        var elements = description.Inputs.Concat(description.Outputs).Concat(description.Primitives).Where(element => element.InSignature).ToList();
        var strings = new List<byte> { 0 };
        var indices = new List<uint>();
        var rows = elements.Select(element => Element(element, strings, indices)).ToList();

        while (strings.Count % sizeof(uint) != 0)
        {
            strings.Add(0);
        }

        AppendWords(output, (uint)strings.Count);
        output.AddRange(strings);
        AppendWords(output, (uint)indices.Count);
        AppendWords(output, [.. indices]);

        if (rows.Count > 0)
        {
            AppendWords(output, ElementBytes);
            rows.ForEach(output.AddRange);
        }

        Dependencies(output, description);

        return [.. output];
    }

    private static byte[] RuntimeInfo(ShaderDescription description)
    {
        var info = new byte[RuntimeInfoBytes];
        var waveSize = description.WaveSize;
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(MinimumWaveOffset), (uint)(waveSize ?? 0));
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(MaximumWaveOffset), waveSize is null ? uint.MaxValue : (uint)waveSize.Value);
        info[StageOffset] = (byte)description.Kind;
        StageInfo(info, description);

        info[InputElementsOffset] = (byte)description.Inputs.Count(element => element.InSignature);
        info[InputElementsOffset + 1] = (byte)description.Outputs.Count(element => element.InSignature);
        info[InputElementsOffset + 2] = (byte)description.Primitives.Count(element => element.InSignature);
        info[InputElementsOffset + 3] = (byte)ViewIdState.Vectors(description.Inputs);
        info[InputElementsOffset + 4] = (byte)ViewIdState.Vectors(description.Outputs);

        if (description.Kind is DxilShaderKind.Compute or DxilShaderKind.Mesh)
        {
            var (x, y, z) = description.Threads;
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(ThreadsOffset), (uint)x);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(ThreadsOffset + sizeof(uint)), (uint)y);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(ThreadsOffset + (2 * sizeof(uint))), (uint)z);
        }

        return info;
    }

    /// <summary>The stage's own fields at the start of the runtime info, and the union after the stage byte.</summary>
    private static void StageInfo(byte[] info, ShaderDescription description)
    {
        var outputs = description.Outputs;

        switch (description.Kind)
        {
            case DxilShaderKind.Vertex:
                info[0] = (byte)(outputs.Any(element => element.Kind == SemanticKind.Position) ? 1 : 0);
                break;
            case DxilShaderKind.Pixel:
                info[0] = (byte)(outputs.Any(element => element.Kind is SemanticKind.Depth or SemanticKind.DepthLessEqual or SemanticKind.DepthGreaterEqual) ? 1 : 0);
                info[1] = (byte)(description.Inputs.Any(element => element.Kind == SemanticKind.SampleIndex
                    || element.Interpolation is Semantics.LinearSample or Semantics.LinearNoPerspectiveSample) ? 1 : 0);
                break;
            case DxilShaderKind.Mesh:
                var mesh = description.Mesh!;
                BinaryPrimitives.WriteUInt32LittleEndian(info, (uint)mesh.SharedBytes);
                BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(MaxVerticesOffset), (ushort)mesh.MaxVertices);
                BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(MaxVerticesOffset + sizeof(ushort)), (ushort)mesh.MaxPrimitives);
                info[UnionOffset] = (byte)ViewIdState.Vectors(description.Primitives);
                info[UnionOffset + 1] = (byte)mesh.Topology;
                break;
            case DxilShaderKind.Geometry:
                var geometry = description.Geometry!;
                BinaryPrimitives.WriteUInt32LittleEndian(info, (uint)geometry.InputPrimitive);
                BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(sizeof(uint)), (uint)geometry.OutputTopology);
                BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(2 * sizeof(uint)), (uint)geometry.StreamMask);
                info[3 * sizeof(uint)] = (byte)(outputs.Any(element => element.Kind == SemanticKind.Position) ? 1 : 0);
                BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(UnionOffset), (ushort)geometry.MaxVertexCount);
                break;
        }
    }

    /// <summary>One element: name and index offsets, rows, start row, columns, kind, type, interpolation.</summary>
    private static byte[] Element(DxilSignatureElement element, List<byte> strings, List<uint> indices)
    {
        var bytes = new byte[ElementBytes];
        uint name = 0;

        if (element.Kind == SemanticKind.Arbitrary)
        {
            name = (uint)strings.Count;
            strings.AddRange(Encoding.ASCII.GetBytes(element.Name));
            strings.Add(0);
        }

        var semanticIndices = Enumerable.Range(element.Index, element.Rows).Select(index => (uint)index).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, name);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(sizeof(uint)), (uint)IndexOffset(indices, semanticIndices));
        bytes[8] = (byte)element.Rows;
        bytes[9] = (byte)Math.Max(element.StartRow, 0);
        bytes[10] = (byte)element.Columns;

        if (element.IsAllocated)
        {
            bytes[10] |= (byte)(Allocated | (element.StartColumn << ColumnShift));
        }

        bytes[11] = (byte)element.Kind;
        bytes[12] = element.RegisterComponentType;
        bytes[13] = element.Interpolation;

        return bytes;
    }

    /// <summary>Where <paramref name="sequence"/> starts in the index table, appending it if it is not there.</summary>
    private static int IndexOffset(List<uint> indices, uint[] sequence)
    {
        for (var start = 0; start + sequence.Length <= indices.Count; start++)
        {
            if (indices.Skip(start).Take(sequence.Length).SequenceEqual(sequence))
            {
                return start;
            }
        }

        indices.AddRange(sequence);

        return indices.Count - sequence.Length;
    }

    /// <summary>The input-to-output table (stream 0) the view-ID state declares: every output on every input.</summary>
    private static void Dependencies(List<byte> output, ShaderDescription description)
    {
        var inputVectors = ViewIdState.Vectors(description.Inputs);
        var outputVectors = ViewIdState.Vectors(description.Outputs);

        if (inputVectors == 0 || outputVectors == 0)
        {
            return;
        }

        var inputs = ViewIdState.Scalars(description.Inputs);
        var outputs = ViewIdState.Scalars(description.Outputs);
        var stride = ((outputVectors * Components) + BitsPerWord - 1) / BitsPerWord;

        for (var input = 0; input < inputVectors * Components; input++)
        {
            for (var word = 0; word < stride; word++)
            {
                AppendWords(output, input < inputs ? ViewIdState.Dependencies(word, outputs) : 0);
            }
        }
    }

    private static void AppendWords(List<byte> output, params uint[] values)
    {
        Span<byte> word = stackalloc byte[sizeof(uint)];

        foreach (var value in values)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(word, value);
            output.AddRange(word.ToArray());
        }
    }
}
