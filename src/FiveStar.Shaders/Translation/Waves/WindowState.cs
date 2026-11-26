// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace FiveStar.Shaders.Translation.Waves;

/// <summary>
/// The facts at one program point: per variable a <see cref="ValueFact"/> (a variable never assigned keeps its
/// declared value, 0 or false, which is exact and uniform), and whether GS_ALLOC_REQ and the barrier ending the first
/// mesh phase happened on every path. <see cref="ValueFact.Held"/> bits stay valid while EXEC only shrinks: a window
/// holding a lane of the smaller EXEC held one of the larger.
/// </summary>
internal sealed class WindowState
{
    private readonly Dictionary<string, ValueFact> _facts;

    public WindowState()
    {
        _facts = new Dictionary<string, ValueFact>(StringComparer.Ordinal);
    }

    private WindowState(Dictionary<string, ValueFact> facts, bool allocated, bool phaseTwo, bool fastWaves)
    {
        _facts = new Dictionary<string, ValueFact>(facts, StringComparer.Ordinal);
        Allocated = allocated;
        PhaseTwo = phaseTwo;
        FastWaves = fastWaves;
    }

    /// <summary>
    /// Fast waves (TranslationOptions.FastWaves): each hardware wave's <c>readfirstlane</c> is taken for its console wave's.
    /// </summary>
    public bool FastWaves { get; init; }

    /// <summary>GS_ALLOC_REQ ran on every path here.</summary>
    public bool Allocated { get; set; }

    /// <summary>A barrier after GS_ALLOC_REQ passed on every path: mesh outputs are written, not dropped.</summary>
    public bool PhaseTwo { get; set; }

    /// <summary>The variables that have facts (assigned somewhere before this point).</summary>
    public IEnumerable<string> Names => _facts.Keys;

    public ValueFact Get(string name)
    {
        if (_facts.TryGetValue(name, out var fact))
        {
            return fact;
        }

        return Declared(name);
    }

    public void Set(string name, ValueFact fact)
    {
        _facts[name] = fact;
    }

    /// <summary>The same facts read without fast waves.</summary>
    public WindowState Strict()
    {
        return new WindowState(_facts, Allocated, PhaseTwo, false);
    }

    public WindowState Clone()
    {
        return new WindowState(_facts, Allocated, PhaseTwo, FastWaves);
    }

    /// <summary>Weakens this state to what also holds in <paramref name="other"/>; true when anything changed.</summary>
    public bool MergeFrom(WindowState other)
    {
        // In place, then the other's names this state lacks in its order: the same facts and insertion order as merging
        // over the union of both name lists, without building it (EntryFacts merges thousands of names per edge).
        var changed = false;

        foreach (var (name, mine) in _facts)
        {
            var merged = ValueFact.Merge(mine, other.Get(name));

            if (merged != mine)
            {
                CollectionsMarshal.GetValueRefOrNullRef(_facts, name) = merged;
                changed = true;
            }
        }

        foreach (var (name, theirs) in other._facts)
        {
            if (_facts.ContainsKey(name))
            {
                continue;
            }

            var declared = Declared(name);
            var merged = ValueFact.Merge(declared, theirs);

            if (merged != declared)
            {
                _facts[name] = merged;
                changed = true;
            }
        }

        if ((Allocated && !other.Allocated) || (PhaseTwo && !other.PhaseTwo))
        {
            Allocated = Allocated && other.Allocated;
            PhaseTwo = PhaseTwo && other.PhaseTwo;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// EXEC is about to change: masks lose their relation to it. When it only shrinks, a value equal on every active
    /// lane still is; otherwise only values equal on every lane stay uniform.
    /// </summary>
    public void ExecChanging(bool shrinks)
    {
        foreach (var name in _facts.Keys)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_facts, name);
            var fact = entry;

            if (fact.Kind is not MaskKind.Uniform)
            {
                fact = fact with { Kind = MaskKind.Other };
            }

            if (!shrinks)
            {
                fact = fact with { Uniform = fact.AllUniform, Held = fact.Exact };
            }

            entry = fact;
        }
    }

    /// <summary>The window is known to hold an active lane: what is the console's in that case now is.</summary>
    public void HoldsActiveLane()
    {
        foreach (var name in _facts.Keys)
        {
            ref var fact = ref CollectionsMarshal.GetValueRefOrNullRef(_facts, name);
            fact = fact with { Exact = fact.Held };
        }

        Set(WindowNames.Active, Get(WindowNames.Active) with { NonEmpty = true });
    }

    /// <summary>The fact of a variable still holding its declared value (0, false, or EXEC all set).</summary>
    private static ValueFact Declared(string name)
    {
        if (name == WindowNames.Active)
        {
            return ValueFact.Scalar with { Kind = MaskKind.ActiveAnd, NonEmpty = true, Full = true };
        }

        if (WindowNames.IsLaneInput(name))
        {
            return ValueFact.ExactValue;
        }

        return ValueFact.Scalar;
    }
}
