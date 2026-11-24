// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace FiveStar.Shaders.Dxil.Container;

/// <summary>
/// ISG1, OSG1 and PSG1 as dxc writes them: count, offset 8, one 32-byte row per element sorted by register (no
/// register last, then by name), then each distinct name once in element order.
/// </summary>
public static class SignatureWriter
{
    private const int HeaderBytes = 8;
    private const int ElementBytes = 32;
    private const int FullMask = 0xF;

    public static byte[] Write(IReadOnlyList<DxilSignatureElement> elements, bool input)
    {
        var listed = elements.Where(element => element.InSignature).ToList();
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        var text = new List<byte>();
        var nameStart = HeaderBytes + (ElementBytes * listed.Count);

        foreach (var element in listed)
        {
            if (!names.ContainsKey(element.Name))
            {
                names[element.Name] = nameStart + text.Count;
                text.AddRange(Encoding.ASCII.GetBytes(element.Name));
                text.Add(0);
            }
        }

        while (text.Count % sizeof(uint) != 0)
        {
            text.Add(0);
        }

        var bytes = new byte[nameStart + text.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)listed.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(sizeof(uint)), HeaderBytes);
        var ordered = listed.OrderBy(element => (uint)element.StartRow).ThenBy(element => names[element.Name]).ToList();

        for (var index = 0; index < ordered.Count; index++)
        {
            var element = ordered[index];
            var row = bytes.AsSpan(HeaderBytes + (ElementBytes * index), ElementBytes);
            var used = element.Usage << Math.Max(element.StartColumn, 0);
            var readWrite = input ? used : ~used & FullMask;

            BinaryPrimitives.WriteUInt32LittleEndian(row[4..], (uint)names[element.Name]);
            BinaryPrimitives.WriteUInt32LittleEndian(row[8..], (uint)element.Index);
            BinaryPrimitives.WriteUInt32LittleEndian(row[12..], element.SystemValue);
            BinaryPrimitives.WriteUInt32LittleEndian(row[16..], element.RegisterComponentType);
            BinaryPrimitives.WriteInt32LittleEndian(row[20..], element.StartRow);
            row[24] = element.Mask;
            row[25] = (byte)readWrite;
        }

        text.CopyTo(bytes, nameStart);

        return bytes;
    }
}
