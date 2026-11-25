// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Translation.Core;
using FiveStar.Shaders.Translation.Structure;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// EntryGuards on control-flow graphs: a cycle entered at several blocks gets one header and the graph becomes
/// reducible (its loops can be found), a reducible graph is left as it is, and a guarded cycle nested in a loop or
/// entered at three blocks is handled too.
/// </summary>
public sealed class EntryGuardTests
{
    [Fact]
    public void A_reducible_graph_is_left_as_it_is()
    {
        // 0 -> 4 -> 4 (a loop) -> 8 (end).
        var graph = Graph(Jump(0, 4), Branch(4, 4, 8), End(8));

        var (guarded, entry, selectors) = EntryGuards.Apply(graph, 0);

        Assert.Same(graph, guarded);
        Assert.Equal(0, entry);
        Assert.Equal(0, selectors);
    }

    [Fact]
    public void A_loop_entered_at_two_blocks_gets_one_header()
    {
        // 0 branches to 8 or falls into 4; 4 -> 8; 8 goes back to 4 or on to 12.
        var graph = Graph(Branch(0, 8, 4), Jump(4, 8), Branch(8, 4, 12), End(12));
        Assert.Throws<StructuringFailedException>(() => Loops(graph, 0));

        var (guarded, entry, selectors) = EntryGuards.Apply(graph, 0);

        Assert.Equal(1, selectors);
        var loops = Loops(guarded, entry);
        Assert.Contains(guarded.Blocks, block => block.Exit.Condition == "(irr_entry0 == 0u)");
        Assert.Single(Enumerable.Range(0, guarded.End), loops.IsHeader);
    }

    [Fact]
    public void A_loop_entered_at_three_blocks_tests_the_selector_twice()
    {
        // 0 -> 8 (b) or 4; 4 -> 12 (c) or 16 (a); a -> b -> c -> a or end.
        var graph = Graph(Branch(0, 8, 4), Branch(4, 12, 16), Jump(16, 8), Jump(8, 12), Branch(12, 16, 20), End(20));

        var (guarded, entry, selectors) = EntryGuards.Apply(graph, 0);

        Assert.Equal(1, selectors);
        Assert.Equal(2, guarded.Blocks.Count(block => block.Exit.Condition?.StartsWith("(irr_entry0", StringComparison.Ordinal) == true));
        _ = Loops(guarded, entry);
    }

    [Fact]
    public void A_two_entry_loop_inside_a_loop_is_guarded_inside_it()
    {
        // 0 -> 4 (outer header): branches to 12 or falls into 8; 8 -> 12; 12 back to 8 or to 16; 16 back to 4 or end.
        var graph = Graph(Jump(0, 4), Branch(4, 12, 8), Jump(8, 12), Branch(12, 8, 16), Branch(16, 4, 20), End(20));

        var (guarded, entry, selectors) = EntryGuards.Apply(graph, 0);

        Assert.Equal(1, selectors);
        var loops = Loops(guarded, entry);
        Assert.Equal(2, Enumerable.Range(0, guarded.End).Count(loops.IsHeader));
    }

    private static LoopForest Loops(FlowGraph graph, int entry)
    {
        return new LoopForest(graph.Successors, new DominatorTree(graph.Successors, entry));
    }

    private static FlowGraph Graph(params CodeBlock[] blocks)
    {
        return new FlowGraph([.. blocks.OrderBy(block => block.Address)]);
    }

    private static CodeBlock Jump(int address, long target)
    {
        return new CodeBlock(address, ["work;", "pc;"], new BlockExit(BlockExitKind.Jump, target, null, null));
    }

    private static CodeBlock Branch(int address, long taken, long next)
    {
        return new CodeBlock(address, ["work;", "pc;"], new BlockExit(BlockExitKind.Conditional, taken, next, "scc"));
    }

    private static CodeBlock End(int address)
    {
        return new CodeBlock(address, ["work;", "pc;"], new BlockExit(BlockExitKind.End, null, null, null));
    }
}
