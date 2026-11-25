// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Shaders.Tests.Cases;
using FiveStar.Shaders.Tests.Toolchain;
using FiveStar.Shaders.Tests.Warp;
using FiveStar.Shaders.Translation;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// The loader's window guard (FIVESTAR_WINDOW_GUARD): a store through a 32-bit pointer would land in the descriptor
/// window; it is dropped and counted in the program's slot of the guard's table.
/// </summary>
public sealed class WindowGuardTests
{
    private const uint TableOffset = 0x100;
    private const uint ProgramId = 5;
    private const int WindowWords = 0x2200 / 4;
    private const int StoreWord = 0x800 / 4;
    private const int Threads = 64;
    private const uint SpanOffset = 0x1000;
    private const int NullView = 8;
    private const int PageWords = 256;

    [WarpFact]
    public void A_store_through_a_32_bit_pointer_is_dropped_and_counted_for_its_program()
    {
        const string Program = """
            v_lshlrev_b32_e32 v1, 2, v41
            v_add_nc_u32_e32 v1, 0x800, v1
            v_mov_b32 v2, 0
            v_mov_b32 v3, 0x1234
            global_store_dword v[1:2], v3, off
            v_mov_b32 v20, 0
            """;
        var testCase = new ComputeCase("cs_window_guard", Program, 1, _ => [Expected.Exact(0)])
        {
            Buffers = [new BufferResource(0, new uint[WindowWords], Writable: false), new BufferResource(1, new uint[WindowWords], Writable: true)],
            Check = written => Guarded(written[1]),
        };

        using var fixture = new WarpFixture(UserDataBinding.RootConstants, $"#define FIVESTAR_WINDOW_GUARD {TableOffset}u\n#define FIVESTAR_PROGRAM_ID {ProgramId}u\n");
        var mismatches = new ComputeCaseRunner(fixture).Run(testCase);

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    /// <summary>
    /// A global store and a buffer store whose high dword is past the user space (0x8005) reach nothing, where the
    /// 15-bit mask aliased them into span 5's mapped page; both are counted in the span table. A store through span 5
    /// itself still lands.
    /// </summary>
    [WarpFact]
    public void A_store_past_the_user_space_reaches_nothing_and_is_counted()
    {
        const string Program = """
            v_lshlrev_b32_e32 v1, 2, v41
            v_mov_b32 v2, 0x8005
            v_mov_b32 v3, 0x1234
            global_store_dword v[1:2], v3, off
            s_mov_b32 s20, 0
            s_mov_b32 s21, 0x8005
            s_mov_b32 s22, -1
            s_mov_b32 s23, 0x11014fac
            buffer_store_dword v3, v1, s[20:23], 0 offen
            v_add_nc_u32_e32 v4, 0x100, v1
            v_mov_b32 v5, 5
            global_store_dword v[4:5], v3, off
            v_mov_b32 v20, 0
            """;
        const ulong Span5 = 0x5_0000_0000;
        const int PageHeap = 12;

        var map = new AddressMap(1, NullView);
        var bytes = new byte[map.Bytes];
        map.Clear(bytes);
        map.Map(bytes, Span5, 0x10000, PageHeap - 1, Span5);

        var testCase = new ComputeCase("cs_span_guard", Program, 1, _ => [Expected.Exact(0)])
        {
            Buffers =
            [
                new BufferResource(0, new uint[WindowWords], Writable: false),
                new BufferResource(1, new uint[WindowWords], Writable: true),
                new BufferResource((int)AddressMap.HeapSlot, MemoryMarshal.Cast<byte, uint>(bytes).ToArray(), Writable: false),
                new BufferResource(PageHeap, new uint[PageWords], Writable: true),
            ],
            Check = written => SpanGuarded(written[1], written[PageHeap]),
        };

        using var fixture = new WarpFixture(UserDataBinding.RootConstants, $"#define FIVESTAR_WINDOW_GUARD {TableOffset}u\n#define FIVESTAR_SPAN_GUARD {SpanOffset}u\n#define FIVESTAR_PROGRAM_ID {ProgramId}u\n");
        var mismatches = new ComputeCaseRunner(fixture).Run(testCase);

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    private static IEnumerable<string> SpanGuarded(uint[] window, uint[] page)
    {
        var aliased = page.Take(Threads).Count(word => word != 0);
        var landed = page.Skip(0x100 / 4).Take(Threads).Count(word => word == 0x1234);
        var slot = (int)((SpanOffset / 4) + (ProgramId * 2));

        if (aliased != 0 || landed != Threads || window[slot] != ProgramId || window[slot + 1] != 2 * Threads)
        {
            yield return $"{aliased} stores aliased into span 5, {landed} landed there; span slot {window[slot]}, {window[slot + 1]} drops";
        }
    }

    /// <summary>No store reached the window, and the program's slot holds its id and one drop per thread.</summary>
    private static IEnumerable<string> Guarded(uint[] window)
    {
        var stored = window.Skip(StoreWord).Take(Threads).Count(word => word != 0);
        var slot = (int)((TableOffset / 4) + (ProgramId * 2));

        if (stored != 0 || window[slot] != ProgramId || window[slot + 1] != Threads)
        {
            yield return $"{stored} stores reached the window; slot {window[slot]}, {window[slot + 1]} drops";
        }
    }
}
