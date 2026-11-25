// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// Which side of a depth twin (the depth texture's plane, or the twin) each command list uses, so the copies between
/// them follow the order lists are submitted in, not the order they were recorded in (lists are recorded on many
/// threads). A list notes, per twin, whether it uses each side before it changes it (it needs that side as the lists
/// submitted before it left it) and how it leaves each side (behind the other, or not). Each twin keeps where the sides
/// stand between submissions (<see cref="DepthTwin.Behind"/>); at submission (<see cref="Plan"/>) a list that needs a side
/// the lists before it left behind gets a prelude copy (<see cref="DepthTwinPrelude"/>), and the twin then stands as
/// the list leaves it. A new twin's side is behind its plane, so the first list that uses it fills it from the plane, on
/// whatever queue that list runs.
/// <para>
/// Within one list the copies are recorded where they are needed (<see cref="UseTwin"/>, <see cref="UsePlane"/>). A
/// depth target bound for a draw counts as written, and a pass that binds a twin's UAV counts as writing the twin.
/// </para>
/// <para>
/// Follows the per-list sides and the prelude of the fork of VIctory by Kelvaroq (victory-fork,
/// crates/victory-gpu/src/host.rs: TwinUse, twin_use, prelude; GPL-3.0-or-later), ported as facts and rewritten here.
/// </para>
/// </summary>
internal static class DepthTwinSides
{
    /// <summary><see cref="DepthTwin.Behind"/> bits: the plane lacks what the twin was written last, or the twin what the plane was.</summary>
    public const int PlaneBehind = 1;
    public const int TwinBehind = 2;

    private static readonly ConcurrentDictionary<nint, ListSides> Lists = new();
    private static readonly object Gate = new();

    /// <summary>What a submitted list needs copied first: into the plane (<c>ToPlane</c>) or into the twin.</summary>
    public readonly record struct Copy(DepthTwin Twin, bool ToPlane);

    /// <summary>
    /// List <paramref name="list"/> runs a pass that writes <paramref name="twin"/>; true when the list must first copy
    /// the plane into the twin itself (it wrote the plane earlier).
    /// </summary>
    public static bool UseTwin(nint list, DepthTwin twin)
    {
        var use = Lists.GetOrAdd(list, _ => new ListSides()).For(twin);
        bool copy = use.TwinKnown && use.TwinIsBehind;

        if (!use.TwinKnown)
        {
            use.NeedsTwin = true;
        }

        use.TwinKnown = true;
        use.TwinIsBehind = false;
        use.PlaneKnown = true;
        use.PlaneIsBehind = true;

        return copy;
    }

    /// <summary>
    /// List <paramref name="list"/> draws with <paramref name="twin"/>'s depth texture as its depth target (in
    /// <paramref name="format"/>); true when the list must first copy the twin into the plane itself (it wrote the twin
    /// earlier).
    /// </summary>
    public static bool UsePlane(nint list, DepthTwin twin, int format)
    {
        twin.DepthFormat = format;
        var use = Lists.GetOrAdd(list, _ => new ListSides()).For(twin);
        bool copy = use.PlaneKnown && use.PlaneIsBehind;

        if (!use.PlaneKnown)
        {
            use.NeedsPlane = true;
        }

        use.PlaneKnown = true;
        use.PlaneIsBehind = false;
        use.TwinKnown = true;
        use.TwinIsBehind = true;

        return copy;
    }

    /// <summary>
    /// <paramref name="list"/> is submitted now, after every list submitted before it: the copies it needs first, added
    /// to <paramref name="copies"/>; each twin then stands as the list leaves it. The list's notes are dropped.
    /// </summary>
    public static void Plan(nint list, List<Copy> copies)
    {
        if (!Lists.TryRemove(list, out var sides))
        {
            return;
        }

        lock (Gate)
        {
            Account(sides, copies);
        }
    }

    /// <summary>Forgets what <paramref name="list"/> noted (it was reset without being submitted).</summary>
    public static void Forget(nint list)
    {
        Lists.TryRemove(list, out _);
    }

    /// <summary>Whether any list holds notes.</summary>
    public static bool Any => !Lists.IsEmpty;

    private static void Account(ListSides sides, List<Copy> copies)
    {
        foreach (var (twin, use) in sides.Uses)
        {
            int behind = twin.Behind;

            if (use.NeedsTwin && (behind & TwinBehind) != 0)
            {
                copies.Add(new Copy(twin, false));
                behind = 0;
            }
            else if (use.NeedsPlane && (behind & PlaneBehind) != 0)
            {
                copies.Add(new Copy(twin, true));
                behind = 0;
            }

            twin.Behind = Leaves(use, behind);
        }
    }

    /// <summary>How the twin stands after the list: each side it used as it left it, the others as they stood.</summary>
    private static int Leaves(SideUse use, int behind)
    {
        int left = behind;

        if (use.PlaneKnown)
        {
            left = (left & ~PlaneBehind) | Bit(use.PlaneIsBehind, PlaneBehind);
        }

        if (use.TwinKnown)
        {
            left = (left & ~TwinBehind) | Bit(use.TwinIsBehind, TwinBehind);
        }

        return left;
    }

    private static int Bit(bool set, int bit)
    {
        if (set)
        {
            return bit;
        }

        return 0;
    }

    /// <summary>One list's notes, per twin; a list is recorded on one thread.</summary>
    private sealed class ListSides
    {
        public Dictionary<DepthTwin, SideUse> Uses { get; } = [];

        public SideUse For(DepthTwin twin)
        {
            lock (Uses)
            {
                if (!Uses.TryGetValue(twin, out var use))
                {
                    use = new SideUse();
                    Uses[twin] = use;
                }

                return use;
            }
        }
    }

    /// <summary>What one list does with one twin's sides.</summary>
    private sealed class SideUse
    {
        public bool NeedsPlane { get; set; }

        public bool NeedsTwin { get; set; }

        public bool PlaneKnown { get; set; }

        public bool PlaneIsBehind { get; set; }

        public bool TwinKnown { get; set; }

        public bool TwinIsBehind { get; set; }
    }
}
