// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// The desktop heap index each view record of the <see cref="DescriptorWindow"/> names, kept in cached memory as the
/// mirror writes the record, and which records name an index someone watches (a depth twin's UAV, a shared-exponent
/// shadow's view). The window itself is write-combined memory: every CPU read of it is uncached, and the per-draw scans
/// of the tables a pipeline reaches (<see cref="Resources.DepthTwins.MarkWriters"/>,
/// <see cref="Resources.SharedExponentAliases.BeforeWork"/>) read tens of thousands of its records per draw, which took
/// half of each of the game's five recording threads in w110. They now visit only the marked records.
/// </summary>
internal sealed unsafe class WindowRecordIndices : IDisposable
{
    /// <summary>View records of the window, up to the samplers (the null table's included).</summary>
    public const uint Slots = DescriptorWindow.SamplerBase / DescriptorWindow.ViewRecordBytes;

    /// <summary>What a record that names no heap index holds (a reserved slot no one watches).</summary>
    public const uint None = 0;

    private const int BitsPerWord = 32;
    private const int GrowthFactor = 2;

    private readonly object _gate = new();
    private readonly uint[] _marks = new uint[(Slots + BitsPerWord - 1) / BitsPerWord];
    private uint* _indices;
    private uint[] _watched = [];
    private volatile bool _any;

    /// <summary>Whether any index is watched: until then the mirror only notes indices.</summary>
    public bool Any => _any;

    /// <summary>
    /// The mirror wrote the view record at <paramref name="slot"/> with dwords 0 to 3 <paramref name="word0"/> to
    /// <paramref name="word3"/>: it names heap index <paramref name="word0"/> when the console fields are 0
    /// (<see cref="DebugLayer.DepthUavs.HeapIndex"/>).
    /// </summary>
    [HotPath]
    public void Written(uint slot, uint word0, uint word1, uint word2, uint word3)
    {
        if (slot >= Slots)
        {
            return;
        }

        uint index = None;

        if (word1 == 0 && word2 == 0 && word3 == 0)
        {
            index = word0;
        }

        Indices()[slot] = index;

        // Ordered before the read of the watched set: a Watch meanwhile either sees this index in its scan or is seen here.
        Interlocked.MemoryBarrier();

        if (_any)
        {
            Mark(slot, Watched(index));
        }
    }

    /// <summary>The heap index the record at <paramref name="slot"/> names, null for none.</summary>
    [HotPath]
    public uint? HeapIndex(uint slot)
    {
        uint index = Indices()[slot];

        if (index == None)
        {
            return null;
        }

        return index;
    }

    /// <summary>Records naming heap index <paramref name="index"/> are marked from now on, those already written too.</summary>
    public void Watch(uint index)
    {
        if (index == None)
        {
            return;
        }

        lock (_gate)
        {
            if (Watched(index))
            {
                return;
            }

            int word = (int)(index / BitsPerWord);

            if (word >= _watched.Length)
            {
                var grown = new uint[Math.Max(word + 1, _watched.Length * GrowthFactor)];
                _watched.CopyTo(grown, 0);
                Volatile.Write(ref _watched, grown);
            }

            Interlocked.Or(ref _watched[word], 1u << (int)(index % BitsPerWord));
            _any = true;
            uint* indices = Indices();

            for (uint slot = 0; slot < Slots; slot++)
            {
                if (indices[slot] == index)
                {
                    Mark(slot, true);
                }
            }
        }
    }

    /// <summary>The slot past a table of <paramref name="records"/> records (at most <paramref name="limit"/>) from <paramref name="first"/>, within the view records.</summary>
    [HotPath]
    public static uint End(uint first, uint records, uint limit)
    {
        if (records > limit)
        {
            records = limit;
        }

        ulong end = (ulong)first + records;

        if (end > Slots)
        {
            end = Slots;
        }

        return (uint)end;
    }

    /// <summary>The first marked slot from <paramref name="from"/> below <paramref name="end"/>; <paramref name="end"/> for none.</summary>
    [HotPath]
    public uint NextMarked(uint from, uint end)
    {
        if (end > Slots)
        {
            end = Slots;
        }

        while (from < end)
        {
            uint word = Volatile.Read(ref _marks[from / BitsPerWord]) >> (int)(from % BitsPerWord);

            if (word != 0)
            {
                uint found = from + (uint)System.Numerics.BitOperations.TrailingZeroCount(word);

                if (found > end)
                {
                    found = end;
                }

                return found;
            }

            from = (from / BitsPerWord * BitsPerWord) + BitsPerWord;
        }

        return end;
    }

    public void Dispose()
    {
        if (_indices != null)
        {
            NativeMemory.Free(_indices);
            _indices = null;
        }
    }

    [HotPath]
    private bool Watched(uint index)
    {
        var watched = Volatile.Read(ref _watched);
        uint word = index / BitsPerWord;

        return index != None && word < watched.Length && (Volatile.Read(ref watched[word]) & (1u << (int)(index % BitsPerWord))) != 0;
    }

    [HotPath]
    private void Mark(uint slot, bool marked)
    {
        uint bit = 1u << (int)(slot % BitsPerWord);
        ref uint word = ref _marks[slot / BitsPerWord];

        if (marked)
        {
            Interlocked.Or(ref word, bit);
        }
        else if ((Volatile.Read(ref word) & bit) != 0)
        {
            Interlocked.And(ref word, ~bit);
        }
    }

    /// <summary>The index of every slot, made zeroed (no index) at first use.</summary>
    [HotPath]
    private uint* Indices()
    {
        var indices = _indices;

        if (indices == null)
        {
            indices = Allocate();
        }

        return indices;
    }

    private uint* Allocate()
    {
        lock (_gate)
        {
            if (_indices == null)
            {
                _indices = (uint*)NativeMemory.AllocZeroed(Slots, sizeof(uint));
            }

            return _indices;
        }
    }
}
