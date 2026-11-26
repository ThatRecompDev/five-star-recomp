// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Shaders.Tests.Reference;
using FiveStar.Shaders.Tests.Warp;
using FiveStar.Shaders.Translation;

namespace FiveStar.Shaders.Tests.Cases;

/// <summary>
/// image_bvh_intersect_ray / image_bvh64_intersect_ray compute cases: each of the 64 threads loads its query (12 dwords)
/// from the table at s[8:9] = 0 and intersects it with the BVH whose T# is in s[4:7]; the result dwords v20..v23 are
/// compared with <see cref="BvhReference"/>.
/// </summary>
public static class BvhCases
{
    private const int RecordDwords = 12;
    private const uint Untouched = 0x12345678;

    /// <summary>The scene's base in game memory: above 4 GiB and above 1 TiB, so both high address bytes count.</summary>
    private const ulong GameBase = 0x27E_0010_0000;

    /// <summary>What a node misses with under FIVESTAR_BVH_MISS: a triangle t_num inf over t_denom 1, a box no child.</summary>
    private static readonly uint[] TriangleMiss = [0x7f800000u, 0x3f800000u, 0u, 0u];
    private static readonly uint[] BoxMiss = [uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue];

    private const string LoadProgram = """
        v_mul_u32_u24_e32 v1, 48, v41
        global_load_dwordx4 v[2:5], v1, s[8:9]
        global_load_dwordx4 v[6:9], v1, s[8:9] offset:16
        global_load_dwordx4 v[10:13], v1, s[8:9] offset:32
        s_waitcnt vmcnt(0)
        """;

    // The 11 address dwords v2..v12 scattered for the NSA form.
    private static readonly int[] Scattered = [31, 35, 30, 33, 38, 32, 40, 34, 36, 39, 37];

    public static IEnumerable<ComputeCase> All()
    {
        return
        [
            Make("bvh_box_sorted", BvhQueries.Boxes(1), new Options()),
            Make("bvh_box_unsorted", BvhQueries.Boxes(1), new Options { Sort = false }),
            Make("bvh_box_grow6", BvhQueries.Boxes(2), new Options { Grow = 6 }),
            Make("bvh_box_grow0_b", BvhQueries.Boxes(2), new Options()),
            Make("bvh_tri_ij", BvhQueries.Triangles(3), new Options()),
            Make("bvh_tri_mode0", BvhQueries.Triangles(3), new Options { ReturnBarycentrics = false }),
            Make("bvh64_mixed", BvhQueries.Mixed(4), new Options { Wide = true }),
            Make("bvh_a16", BvhQueries.Half(5), new Options { Half = true }),
            Make("bvh64_a16", BvhQueries.Half(6), new Options { Wide = true, Half = true }),
            Make("bvh_exec", BvhQueries.Mixed(7), new Options { ActiveThreads = 40 }),
            Make("bvh_nsa", BvhQueries.Mixed(8), new Options { NonSequential = true }),
        ];
    }

    public static ComputeCase Find(string name)
    {
        return All().Single(testCase => testCase.Name == name);
    }

    /// <summary>
    /// The mixed queries against a BVH base whose memory is garbage, for programs made with FIVESTAR_BVH_MISS (no
    /// acceleration structure built): every box node reports no child (all invalid), every triangle node no hit
    /// (t_num inf, t_denom 1), whatever the nodes hold.
    /// </summary>
    public static ComputeCase Garbage()
    {
        var queries = BvhQueries.Mixed(9);
        var memory = BvhScene.Build();
        var random = new Random(63);
        int words = memory.Words().Length;

        for (var index = BvhScene.Base / 4; index < words; index++)
        {
            memory.WriteWord(4 * index, (uint)random.NextInt64(uint.MaxValue));
        }

        var options = new Options();
        var records = queries.SelectMany(query => Record(query, options, 0)).ToArray();

        for (var index = 0; index < records.Length; index++)
        {
            memory.WriteWord(4 * index, records[index]);
        }

        var miss = queries.Select(Miss).ToList();

        return new ComputeCase("bvh_garbage_miss", Program(options), 4, thread => [.. miss[thread.Id].Select(Expected.Exact)])
        {
            SecondDescriptor = BvhReference.Descriptor(BvhScene.Base, options.Sort, options.Grow, options.ReturnBarycentrics),
            User = [0, 0, 0, 0],
            Buffers = [new BufferResource(0, memory.Words(), Writable: false)],
        };
    }

    /// <summary>
    /// The mixed queries against the scene in game memory: the T# names a console address (bits 47:40 in dword1), whose
    /// first 64 KiB page the address map maps to a buffer holding the scene; every fourth query's node is moved one page
    /// on, into the unmapped page after it, and misses (a box no child, a triangle no hit) instead of reading zeros.
    /// </summary>
    public static ComputeCase GameMemory()
    {
        const ulong Page = 0x10000;
        const int SceneHeap = 10;
        const int NullView = 8;
        var queries = BvhQueries.Mixed(10);
        var memory = BvhScene.Build();
        var options = new Options();

        var records = queries.SelectMany(query => Record(query, options, 0)).ToArray();
        var moved = queries.Select((query, index) => index % 4 == 3).ToArray();

        for (var index = 0; index < records.Length; index++)
        {
            if (index % RecordDwords == 0 && moved[index / RecordDwords])
            {
                records[index] += (uint)(Page >> 3);
            }

            memory.WriteWord(4 * index, records[index]);
        }

        var window = BvhReference.Descriptor(BvhScene.Base, options.Sort, options.Grow, options.ReturnBarycentrics);
        var expected = queries.Select((query, index) => Expect(memory, window, query, moved[index])).ToList();

        var map = new AddressMap(1, NullView);
        var bytes = new byte[map.Bytes];
        map.Clear(bytes);
        map.Map(bytes, GameBase, Page, SceneHeap, GameBase - (ulong)BvhScene.Base);

        return new ComputeCase("bvh_game_memory_unmapped_miss", Program(options), 4, thread => [.. expected[thread.Id].Select(Expected.Exact)])
        {
            SecondDescriptor = GameDescriptor(window),
            User = [0, 0, 0, 0],
            Buffers =
            [
                new BufferResource(0, memory.Words(), Writable: false),
                new BufferResource((int)AddressMap.HeapSlot, MemoryMarshal.Cast<byte, uint>(bytes).ToArray(), Writable: false),
                new BufferResource(SceneHeap, memory.Words(), Writable: false),
            ],
        };
    }

    /// <summary>What a query gives: the reference over the scene, or a miss when its node was moved into the unmapped page.</summary>
    private static uint[] Expect(BvhMemory memory, uint[] descriptor, BvhQuery query, bool moved)
    {
        if (moved)
        {
            return Miss(query);
        }

        return BvhReference.Intersect(memory, descriptor, query.Node, query.Ray);
    }

    /// <summary>The window T# with its base moved to <see cref="GameBase"/>: bits 39:8 in dword 0, bits 47:40 in dword 1's low byte.</summary>
    private static uint[] GameDescriptor(uint[] window)
    {
        return [unchecked((uint)(GameBase >> 8)), window[1] | (uint)(GameBase >> 40), window[2], window[3]];
    }

    private static ComputeCase Make(string name, List<BvhQuery> queries, Options options)
    {
        var memory = BvhScene.Build();
        var random = new Random(name.Length);
        var words = queries.SelectMany(query => Record(query, options, (uint)random.NextInt64(uint.MaxValue))).ToArray();

        for (var index = 0; index < words.Length; index++)
        {
            memory.WriteWord(4 * index, words[index]);
        }

        var descriptor = BvhReference.Descriptor(BvhScene.Base, options.Sort, options.Grow, options.ReturnBarycentrics);
        var expected = queries.Select(query => BvhReference.Intersect(memory, descriptor, query.Node, query.Ray)).ToList();

        return new ComputeCase(name, Program(options), 4, thread => Expect(expected, thread, options))
        {
            SecondDescriptor = descriptor,
            User = [0, 0, 0, 0],
            Buffers = [new BufferResource(0, memory.Words(), Writable: false)],
        };
    }

    private static Expected[] Expect(List<uint[]> expected, ComputeThread thread, Options options)
    {
        if (options.ActiveThreads is not null && thread.Id >= options.ActiveThreads)
        {
            return [Untouched, Untouched, Untouched, Untouched];
        }

        return [.. expected[thread.Id].Select(Expected.Exact)];
    }

    /// <summary>The result of a query's node under FIVESTAR_BVH_MISS: node types 0 to 3 are triangles.</summary>
    private static uint[] Miss(BvhQuery query)
    {
        if ((query.Node & 7u) < 4u)
        {
            return TriangleMiss;
        }

        return BoxMiss;
    }

    /// <summary>One query as 12 dwords: node (+ node_hi for bvh64), extent, origin, direction and inverse direction (a16: packed f16).</summary>
    private static uint[] Record(BvhQuery query, Options options, uint nodeHigh)
    {
        var ray = query.Ray;
        var record = new List<uint> { query.Node };

        if (options.Wide)
        {
            record.Add(nodeHigh);
        }

        record.Add(BvhFloat.Bits(ray.Extent));
        record.AddRange(ray.Origin.Select(BvhFloat.Bits));

        if (options.Half)
        {
            var (d, inverse) = (ray.Direction.Select(HalfBits).ToArray(), ray.InverseDirection.Select(HalfBits).ToArray());
            record.AddRange([FloatBits.Pack(d[0], d[1]), FloatBits.Pack(d[2], inverse[0]), FloatBits.Pack(inverse[1], inverse[2])]);
        }
        else
        {
            record.AddRange(ray.Direction.Select(BvhFloat.Bits));
            record.AddRange(ray.InverseDirection.Select(BvhFloat.Bits));
        }

        record.AddRange(Enumerable.Repeat(0u, RecordDwords - record.Count));

        return [.. record];
    }

    private static uint HalfBits(double value)
    {
        return FloatBits.Half(value);
    }

    private static string Program(Options options)
    {
        var addressCount = options.Wide ? 12 : 11;
        var mnemonic = options.Wide ? "image_bvh64_intersect_ray" : "image_bvh_intersect_ray";
        var lines = new List<string> { LoadProgram };

        if (options.Half)
        {
            addressCount -= 3;
        }

        var intersect = $"{mnemonic} v[20:23], v[2:{2 + addressCount - 1}], s[4:7]";

        if (options.Half)
        {
            intersect += " a16";
        }

        if (options.NonSequential)
        {
            lines.AddRange(Scattered.Select((register, index) => $"v_mov_b32 v{register}, v{2 + index}"));
            intersect = $"image_bvh_intersect_ray v[20:23], [{string.Join(", ", Scattered.Select(register => $"v{register}"))}], s[4:7]";
        }

        return string.Join("\n", Masked(lines, intersect, options.ActiveThreads));
    }

    /// <summary>With an active-thread limit: v20..v23 preset, EXEC = threads below the limit around the intersect.</summary>
    private static List<string> Masked(List<string> lines, string intersect, int? activeThreads)
    {
        if (activeThreads is null)
        {
            lines.AddRange([intersect, "s_waitcnt vmcnt(0)"]);

            return lines;
        }

        lines.AddRange(Enumerable.Range(20, 4).Select(register => $"v_mov_b32 v{register}, 0x{Untouched:x8}"));
        lines.AddRange([$"v_cmp_gt_u32_e32 vcc_lo, {activeThreads}, v41", "s_and_saveexec_b32 s20, vcc_lo", intersect, "s_waitcnt vmcnt(0)"]);
        lines.Add("s_mov_b32 exec_lo, s20");

        return lines;
    }

    /// <summary>How a case issues the intersect and which T# it uses.</summary>
    private sealed record Options
    {
        public bool Sort { get; init; } = true;

        public int Grow { get; init; }

        public bool ReturnBarycentrics { get; init; } = true;

        public bool Wide { get; init; }

        public bool Half { get; init; }

        public int? ActiveThreads { get; init; }

        public bool NonSequential { get; init; }
    }
}
