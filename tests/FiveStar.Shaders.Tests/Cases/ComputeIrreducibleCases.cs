// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Shaders.Tests.Cases;

/// <summary>
/// Loops entered at several blocks (irreducible control flow), which the structured form now gives one entry guard
/// each: entered at either block, with three entries, and nested in a reducible loop that alternates the entry.
/// </summary>
public static class ComputeIrreducibleCases
{
    // A loop over .Lfirst and .Lsecond entered at .Lsecond when s8 is 0, at .Lfirst otherwise; five rounds of .Lsecond.
    private const string TwoEntries = """
        v_mov_b32 v20, 0
        v_mov_b32 v21, 0
        s_mov_b32 s20, 0
        s_cmp_eq_u32 s8, 0
        s_cbranch_scc1 .Lsecond
        .Lfirst:
        v_add_nc_u32_e32 v20, 1, v20
        .Lsecond:
        v_add_nc_u32_e32 v21, 2, v21
        s_add_u32 s20, s20, 1
        s_cmp_lt_u32 s20, 5
        s_cbranch_scc1 .Lfirst
        """;

    // Entered at .La, .Lb or .Lc for s8 0, 1 or 2; three rounds of .Lc, each block adding its own digit.
    private const string ThreeEntries = """
        v_mov_b32 v20, 0
        s_mov_b32 s20, 0
        s_cmp_eq_u32 s8, 1
        s_cbranch_scc1 .Lb
        s_cmp_eq_u32 s8, 2
        s_cbranch_scc1 .Lc
        .La:
        v_add_nc_u32_e32 v20, 1, v20
        .Lb:
        v_add_nc_u32_e32 v20, 16, v20
        .Lc:
        v_add_nc_u32_e32 v20, 0x100, v20
        s_add_u32 s20, s20, 1
        s_cmp_lt_u32 s20, 3
        s_cbranch_scc1 .La
        """;

    // Four rounds of a reducible outer loop; each enters the inner two-block loop at .Lsecond on even rounds and at
    // .Lfirst on odd ones, two rounds of .Lsecond each: 33 + 34 + 33 + 34.
    private const string Nested = """
        v_mov_b32 v20, 0
        s_mov_b32 s22, 0
        .Louter:
        s_mov_b32 s20, 0
        s_and_b32 s23, s22, 1
        s_cmp_eq_u32 s23, 0
        s_cbranch_scc1 .Lsecond
        .Lfirst:
        v_add_nc_u32_e32 v20, 1, v20
        .Lsecond:
        v_add_nc_u32_e32 v20, 16, v20
        s_add_u32 s20, s20, 1
        s_cmp_lt_u32 s20, 2
        s_cbranch_scc1 .Lfirst
        s_add_u32 s22, s22, 1
        s_cmp_lt_u32 s22, 4
        s_cbranch_scc1 .Louter
        """;

    public static IEnumerable<ComputeCase> All()
    {
        return
        [
            new ComputeCase("cs_irreducible_second", TwoEntries, 2, _ => [4u, 10u]) { User = [0] },
            new ComputeCase("cs_irreducible_first", TwoEntries, 2, _ => [5u, 10u]) { User = [1] },
            new ComputeCase("cs_irreducible_three_a", ThreeEntries, 1, _ => [0x333u]) { User = [0] },
            new ComputeCase("cs_irreducible_three_b", ThreeEntries, 1, _ => [0x332u]) { User = [1] },
            new ComputeCase("cs_irreducible_three_c", ThreeEntries, 1, _ => [0x322u]) { User = [2] },
            new ComputeCase("cs_irreducible_nested", Nested, 1, _ => [134u]),
        ];
    }
}
