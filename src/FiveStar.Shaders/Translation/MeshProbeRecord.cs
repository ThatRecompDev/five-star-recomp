// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Shaders.Translation;

/// <summary>
/// What a probed mesh group wrote (<see cref="MeshProbe"/>, rdna_prelude probe_end): the program's id, its group, the
/// vertex and primitive counts it gave SetMeshOutputCounts (before the clamp to the declared maximum), its culled
/// primitives (count, and which of primitives 0 to 31), vertex 0's position when written, and per mask the load sites
/// (bit = site, 31 for any past it) whose range check failed, that read an unmapped page of the address map, or that
/// read through a record naming no descriptor.
/// </summary>
public readonly record struct MeshProbeRecord(
    uint Id,
    (uint X, uint Y, uint Z) Group,
    uint Vertices,
    uint Primitives,
    uint Culled,
    uint CullMask,
    (float X, float Y, float Z, float W)? Position,
    uint RangeFailed,
    uint Unmapped,
    uint NullRecord)
{
    public const int Dwords = 16;

    private const int PositionWritten = 1;
    private const int GroupX = 1;
    private const int GroupY = 2;
    private const int GroupZ = 3;
    private const int VerticesWord = 4;
    private const int PrimitivesWord = 5;
    private const int CulledWord = 6;
    private const int CullMaskWord = 7;
    private const int PositionX = 8;
    private const int PositionY = 9;
    private const int PositionZ = 10;
    private const int PositionW = 11;
    private const int FlagsWord = 12;
    private const int RangeWord = 13;
    private const int UnmappedWord = 14;
    private const int NullRecordWord = 15;

    /// <summary>A record from its 16 dwords: id, group x y z, vertices, primitives, culled, cull mask, position x y z w, flags, masks.</summary>
    public static MeshProbeRecord Read(ReadOnlySpan<uint> words)
    {
        (float X, float Y, float Z, float W)? position = null;

        if ((words[FlagsWord] & PositionWritten) != 0)
        {
            position = (Float(words, PositionX), Float(words, PositionY), Float(words, PositionZ), Float(words, PositionW));
        }

        return new MeshProbeRecord(
            words[0],
            (words[GroupX], words[GroupY], words[GroupZ]),
            words[VerticesWord],
            words[PrimitivesWord],
            words[CulledWord],
            words[CullMaskWord],
            position,
            words[RangeWord],
            words[UnmappedWord],
            words[NullRecordWord]);
    }

    private static float Float(ReadOnlySpan<uint> words, int at)
    {
        return BitConverter.UInt32BitsToSingle(words[at]);
    }
}
