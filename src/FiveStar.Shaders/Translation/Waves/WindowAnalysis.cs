// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;
using FiveStar.Shaders.Translation.Core;

namespace FiveStar.Shaders.Translation.Waves;

/// <summary>
/// Whether a translated mesh program gives console results on a hardware wave narrower than its console wave, where the
/// hardware wave runs a window of consecutive console lanes and the other lanes of the console wave are live threads of
/// other hardware waves. A forward pass over the blocks tracks which values are the console's (<see cref="ValueFact"/>):
/// wave reductions (<c>vw_any</c>, <c>vw_count</c>, <c>vw_ballot</c>) see only the window, so their results are not,
/// unless they are never read; <c>readfirstlane</c> is when its value is equal on all active lanes and the window holds
/// one. Every branch, output, store and message must read only console values, except a branch on "any active lane"
/// that a window without active lanes may take the other way than its console wave: that is fine when both paths, run
/// with the window's lanes inactive, change nothing the window reads after they meet (<see cref="SkipRegion"/>). Lanes that share LDS or memory without a
/// barrier between them are not handled: those windows would need to run in step.
/// </summary>
internal sealed partial class WindowAnalysis
{
    private const int StepsPerBlock = 64;

    private readonly WindowGraph _graph;
    private readonly IReadOnlyList<string> _prologue;
    private readonly int _threads;
    private readonly Dictionary<int, WindowState> _entries = [];
    private readonly Dictionary<int, LaneBranch> _laneBranches = [];

    // The one-lane mode of LaneBranches: every wave operation gives console results, only the branches are judged.
    private bool _perLane;

    public WindowAnalysis(IReadOnlyList<CodeBlock> blocks, IReadOnlyList<string> prologue, int threads)
    {
        _graph = new WindowGraph(blocks);
        _prologue = prologue;
        _threads = threads;
    }

    /// <summary>Null when every window gives console results, else the first reason found why not.</summary>
    public string? Check()
    {
        var shared = _graph.Blocks.SelectMany(block => block.Statements).FirstOrDefault(statement => statement.IsShared);

        if (shared is not null)
        {
            return $"lanes share memory: {shared.Text}";
        }

        var start = new WindowState();
        var transfer = new StatementTransfer(start, _threads);

        foreach (var line in _prologue)
        {
            transfer.Apply(WindowStatement.Parse(line));
        }

        return Solve(start);
    }

    /// <summary>
    /// For the superstep form, where every wave operation gives console results: the branches on "any active lane" each
    /// thread may decide by its own lane, a window of one. A lane outside the mask is inactive, so it may skip the path its
    /// console wave runs when both paths, run with it inactive, change nothing it reads after they meet
    /// (<see cref="SkipRegion"/>). Whether the paths hold a step is for the caller (<see cref="LaneBranches"/>).
    /// </summary>
    public IReadOnlyCollection<LaneBranch> PerLaneBranches()
    {
        _perLane = true;
        var start = new WindowState();
        var transfer = new StatementTransfer(start, _threads);

        foreach (var line in _prologue)
        {
            transfer.Apply(WindowStatement.Parse(line));
        }

        if (Solve(start) is not null)
        {
            return [];
        }

        return _laneBranches.Values;
    }

    private string? Solve(WindowState start)
    {
        _entries[_graph.Entry] = start;
        var pending = new Queue<int>([_graph.Entry]);
        var limit = StepsPerBlock * (_graph.Blocks.Count() + 1);

        for (var step = 0; pending.Count > 0; step++)
        {
            if (step > limit)
            {
                return "the analysis did not settle";
            }

            var block = _graph.Find(pending.Dequeue());

            if (block is null)
            {
                continue;
            }

            var reason = Visit(block, pending);

            if (reason is not null)
            {
                return $"{Hlsl.Hex(block.Address)}: {reason}";
            }
        }

        return null;
    }

    private string? Visit(WindowBlock block, Queue<int> pending)
    {
        var state = _entries[block.Address].Clone();
        var transfer = new StatementTransfer(state, _threads);

        foreach (var statement in block.Statements)
        {
            var reason = transfer.Apply(statement);

            if (reason is not null && !_perLane)
            {
                return reason;
            }
        }

        var exit = block.Exit;

        if (exit.Kind == BlockExitKind.Yield)
        {
            state.PhaseTwo |= state.Allocated;
        }

        if (exit.Kind != BlockExitKind.Conditional)
        {
            block.Successors.ToList().ForEach(successor => Propagate(successor, state, pending));

            return null;
        }

        return Branch(block, state, pending);
    }

    private string? Branch(WindowBlock block, WindowState state, Queue<int> pending)
    {
        var exit = block.Exit;
        var condition = exit.Condition ?? string.Empty;
        var (target, next) = ((int)exit.Target!.Value, (int)exit.Next!.Value);

        if (_perLane)
        {
            Judge(block, state);
            Propagate(target, state, pending);
            Propagate(next, state, pending);

            return null;
        }

        var fact = ValueRules.Condition(condition, state);

        if (fact.IsExact)
        {
            Propagate(target, state, pending);
            Propagate(next, state, pending);

            return null;
        }

        var any = AnyPattern().Match(condition);

        if (!any.Success || !IsActiveTest(any.Groups["mask"].Value, state))
        {
            return HeldBranch(block, state, pending, fact.IsHeld);
        }

        var (some, none) = (target, next);

        if (any.Groups["not"].Value.Length > 0)
        {
            (some, none) = (next, target);
        }

        var join = _graph.Join(block.Address);

        if (join is null)
        {
            return $"branch on {condition}: its paths meet only at the end";
        }

        var reason = new SkipRegion(_graph, some, join.Value, state, true).Check() ?? new SkipRegion(_graph, none, join.Value, state, false).Check();

        if (reason is not null)
        {
            return reason;
        }

        var holding = state.Clone();
        holding.HoldsActiveLane();
        Propagate(none, state, pending);
        Propagate(some, holding, pending);

        return null;
    }

    /// <summary>
    /// Records whether a branch on "any lane of a mask" may be decided by each lane's own bit, under the facts of its
    /// last visit (the weakest): the mask is the same on every lane, or it is active and a uniform value, so a lane
    /// outside it is inactive or its whole console wave is outside it.
    /// </summary>
    private void Judge(WindowBlock block, WindowState state)
    {
        _laneBranches.Remove(block.Address);
        var exit = block.Exit;
        var any = LaneAnyPattern().Match(exit.Condition ?? string.Empty);

        if (!any.Success)
        {
            return;
        }

        var (some, none) = ((int)exit.Target!.Value, (int)exit.Next!.Value);

        if (any.Groups["not"].Value.Length > 0)
        {
            (some, none) = (none, some);
        }

        var mask = any.Groups["mask"].Value;
        var kind = state.Get(mask).Kind;

        if (mask != WindowNames.Active && kind == MaskKind.Uniform)
        {
            _laneBranches[block.Address] = new LaneBranch(block.Address, mask, some, none, null);

            return;
        }

        var join = _graph.Join(block.Address);

        if ((mask != WindowNames.Active && kind != MaskKind.ActiveAnd) || join is null)
        {
            return;
        }

        if (new SkipRegion(_graph, some, join.Value, state, true).Check() is null && new SkipRegion(_graph, none, join.Value, state, false).Check() is null)
        {
            _laneBranches[block.Address] = new LaneBranch(block.Address, mask, some, none, join);
        }
    }

    /// <summary>
    /// A condition that is the console's when the window holds an active lane: a window without one may take either
    /// path, so both must leave its state as they found it, and neither may hold what only the console runs.
    /// </summary>
    private string? HeldBranch(WindowBlock block, WindowState state, Queue<int> pending, bool held)
    {
        var exit = block.Exit;
        var condition = exit.Condition ?? string.Empty;
        var join = _graph.Join(block.Address);

        if (!held || !state.Get(WindowNames.Active).IsExact || join is null)
        {
            return $"branch on {condition}";
        }

        var (target, next) = ((int)exit.Target!.Value, (int)exit.Next!.Value);
        var reason = new SkipRegion(_graph, target, join.Value, state, false).Check() ?? new SkipRegion(_graph, next, join.Value, state, false).Check();

        if (reason is not null)
        {
            return $"branch on {condition}: {reason}";
        }

        Propagate(target, state, pending);
        Propagate(next, state, pending);

        return null;
    }

    /// <summary>The mask is EXEC, or EXEC and a uniform console value: "any lane of it" means "any active lane".</summary>
    private static bool IsActiveTest(string mask, WindowState state)
    {
        var fact = state.Get(mask);
        var active = state.Get(WindowNames.Active);

        return active.IsExact && fact.IsHeld && (mask == WindowNames.Active || fact.Kind == MaskKind.ActiveAnd);
    }

    private void Propagate(int successor, WindowState state, Queue<int> pending)
    {
        if (!_entries.TryGetValue(successor, out var known))
        {
            _entries[successor] = state.Clone();
            pending.Enqueue(successor);

            return;
        }

        if (known.MergeFrom(state))
        {
            pending.Enqueue(successor);
        }
    }

    [GeneratedRegex(@"^(?<not>!?)vw_any\((?<mask>[A-Za-z_]\w*)\)$")]
    private static partial Regex AnyPattern();

    // A branch condition as the translator writes it: the mask may be parenthesized (vccz).
    [GeneratedRegex(@"^(?<not>!?)vw_any\((?:\((?<mask>[A-Za-z_]\w*)\)|(?<mask>[A-Za-z_]\w*))\)$")]
    private static partial Regex LaneAnyPattern();
}
