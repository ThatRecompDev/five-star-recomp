// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Translation.Core;
using FiveStar.Shaders.Translation.Stages;

namespace FiveStar.Shaders.Translation.Waves;

/// <summary>
/// The body of a mesh program in the superstep form: the blocks of <see cref="SuperstepSplitter"/> in a dispatch loop that
/// runs each thread to its next stop, then one group step (<c>vw_ss_step</c>) per round, until every thread has ended. A
/// two-phase program leaves the first phase (outputs dropped) at the first s_barrier released after GS_ALLOC_REQ, then
/// calls SetMeshOutputCounts; every barrier stays outside the dispatch switch. With the carry
/// (<see cref="TranslationState.SuperstepCarry"/>) registers pass between cases through <see cref="SuperstepCarry"/>'s
/// slots and the cases sit in a tree of small switches by address, so a value is joined over a few cases, not thousands.
/// </summary>
internal static class SuperstepWriter
{
    private const string Indent = "    ";

    // Cases per switch of the carry form's dispatch tree.
    private const int TreeLeaf = 8;

    // The rounds are bounded (LoopCaps.Rounds): every thread counts each round, so all leave on the same one. A round
    // runs at least one block or idle step per thread, so before this only ss_steps (2^24) bounded them.
    private const string RoundBound = "    if (++ss_rounds > SS_ROUNDS) break;";

    private static readonly char[] Separators = [' ', '(', ')', '[', ']', ',', ';', '.', '=', '<', '>', '&', '|', '+', '-', '*', '/', '!', '?', ':', '{', '}'];

    private static readonly string[] Step = ["    if (!vw_ss_step(pc, pc_resume)) break;", RoundBound];

    // Phase one ends on GS_ALLOC_REQ's flag as read in this step: every thread reads it, then meets a barrier before
    // any can run on and set it, so all leave together. Read without that barrier, a thread that resumed first could set
    // the flag while a slower one had not read it yet: the two left the loop at different rounds and the group's next
    // barrier never filled (DEVICE_HUNG on the RTX 4090).
    private static readonly string[] PhaseOneStep =
    [
        "    if (!vw_ss_step(pc, pc_resume)) break;", RoundBound, "    bool ms_alloc = vw_ss_released() && g_ms_alloc != 0;",
        "    GroupMemoryBarrierWithGroupSync();", "    if (ms_alloc) break;",
    ];

    public static List<string> Body(TranslationState state, IReadOnlyList<string> hoisted, MeshDeclarations mesh)
    {
        var lines = new List<string> { "  vw_ss_begin(in_gi);" };
        lines.AddRange(hoisted);
        lines.Add("  uint pc = 0, pc_resume = 0, ss_steps = 0, ss_rounds = 0;");
        lines.Add($"  const uint SS_ROUNDS = {LoopGuard.RoundsHex(state.Stage, state.Loops)};");

        if (state.SuperstepCarry)
        {
            var carry = SuperstepCarry.Plan(state.Blocks, Registers(state, hoisted), Pinned(state, mesh));
            lines.Add($"  // carry: {carry.Size.Registers} registers in {carry.Size.Slots} slots");
            lines.AddRange(carry.Declarations);
            lines.AddRange(carry.Entry);
            state.Blocks.Clear();
            state.Blocks.AddRange(carry.Blocks);
        }

        if (!state.MeshYield)
        {
            lines.AddRange(Round(state, false, Step));

            return lines;
        }

        lines.AddRange(Round(state, true, PhaseOneStep));
        lines.Add($"  SetMeshOutputCounts(min(g_ms_nv, {mesh.MaxVertices}u), min(g_ms_np, {mesh.MaxPrimitives}u));");
        lines.AddRange(Round(state, false, Step));
        lines.AddRange(mesh.CutEpilogue("ss_rounds > SS_ROUNDS"));

        return lines;
    }

    /// <summary>The function-scope registers and their types: VGPRs and SGPRs, lane masks, hoisted temporaries.</summary>
    private static Dictionary<string, string> Registers(TranslationState state, IReadOnlyList<string> hoisted)
    {
        var registers = state.Variables.ToDictionary(key => key.Name, _ => "uint");
        var masks = state.Masks.Union(state.MasksEver).Where(key => key != SpecialRegisters.Exec).Select(key => key.MaskName);

        foreach (var mask in masks.Append("active").Append("scc"))
        {
            registers[mask] = "bool";
        }

        foreach (var declaration in hoisted.Select(line => line.Trim().TrimEnd(';').Split(' ')))
        {
            registers[declaration[1]] = declaration[0];
        }

        return registers;
    }

    /// <summary>Names the code after the dispatch loop reads: the staging epilogue of a one-phase mesh program.</summary>
    private static HashSet<string> Pinned(TranslationState state, MeshDeclarations mesh)
    {
        if (state.Stage != ShaderStage.Mesh || state.MeshYield)
        {
            return [];
        }

        return mesh.StagingEpilogue().SelectMany(line => line.Split(Separators, StringSplitOptions.RemoveEmptyEntries)).ToHashSet();
    }

    /// <summary>
    /// One dispatch loop and its step. A thread that has run more blocks than the stage's dispatch steps allow ends
    /// there (<see cref="LoopGuard"/>): it still meets every step's barrier, as an ended thread, so a console loop fed
    /// garbage cannot hold the group. This form had no bound before.
    /// </summary>
    private static List<string> Round(TranslationState state, bool dropOutputs, IEnumerable<string> end)
    {
        // Every thread enters the dispatch loop (a stopped one through the idle case, which gives its pc back), so all
        // reach the step's barrier on the same path: otherwise WARP waits at the barrier for threads that skipped the
        // loop and are at another copy of it.
        var lines = new List<string>
        {
            "  [loop] for (;;) {", "  uint pc_stopped = pc;", "  if (pc >= PC_WAVE) { pc = PC_IDLE; }", "  [loop] while (pc < PC_WAVE) {",
            $"    if (++ss_steps > {LoopGuard.DispatchHex(state.Stage, state.Loops)}) {{ pc = PC_END; break; }}",
        };

        if (state.SuperstepCarry)
        {
            lines.Add("    if (pc == PC_IDLE) { pc = pc_stopped; } else {");
            Tree(lines, [.. state.Blocks.OrderBy(block => (uint)block.Address)], dropOutputs);
            lines.AddRange(["    }", "  }"]);
        }
        else
        {
            lines.AddRange(["    switch (pc) {", "    case PC_IDLE: { pc = pc_stopped; } break;"]);
            Cases(lines, state.Blocks, dropOutputs);
            lines.AddRange(["    default: pc = PC_END; break;", "    }", "  }"]);
        }

        lines.AddRange(end);
        lines.Add("  }");

        return lines;
    }

    /// <summary>
    /// The cases split by address into a binary tree of switches of at most <see cref="TreeLeaf"/> cases: a value written
    /// in one case is joined at its leaf's switch and up the tree, not at one join over every case.
    /// </summary>
    private static void Tree(List<string> lines, List<CodeBlock> blocks, bool dropOutputs)
    {
        if (blocks.Count <= TreeLeaf)
        {
            lines.Add("    switch (pc) {");
            Cases(lines, blocks, dropOutputs);
            lines.AddRange(["    default: pc = PC_END; break;", "    }"]);

            return;
        }

        var middle = blocks.Count >> 1;
        lines.Add($"    if (pc < {Hlsl.Hex(blocks[middle].Address)}) {{");
        Tree(lines, blocks[..middle], dropOutputs);
        lines.Add("    } else {");
        Tree(lines, blocks[middle..], dropOutputs);
        lines.Add("    }");
    }

    private static void Cases(List<string> lines, IEnumerable<CodeBlock> blocks, bool dropOutputs)
    {
        foreach (var block in blocks)
        {
            lines.Add($"    case {Hlsl.Hex(block.Address)}: {{");
            lines.AddRange(block.Lines.Where(line => !dropOutputs || !MeshDeclarations.WritesOutput(line)).Select(line => Indent + line));
            lines.Add("    } break;");
        }
    }
}
