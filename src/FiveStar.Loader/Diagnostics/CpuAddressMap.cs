// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// What a <c>--cpu-sample</c> stack's addresses are: mapped images (the game), native OS-loaded modules, the waiting
/// system-call stubs, and everything else, which is managed (JIT code and the precompiled assemblies: the loader's
/// shims). <see cref="Classify"/> turns a captured stack into a <see cref="CpuSample"/>.
/// </summary>
internal sealed class CpuAddressMap
{
    private readonly CodeRange[] _mapped;
    private readonly CodeRange[] _native;
    private readonly CodeRange[] _waits;
    private readonly Func<ulong, ulong> _functionStart;

    /// <param name="mapped">The images the loader mapped.</param>
    /// <param name="native">The OS-loaded modules that are not managed assemblies.</param>
    /// <param name="waits">The waiting system-call stubs (<see cref="WaitStubs"/>).</param>
    /// <param name="functionStart">The start of the function around a mapped address (its .pdata entry).</param>
    public CpuAddressMap(CodeRange[] mapped, CodeRange[] native, CodeRange[] waits, Func<ulong, ulong> functionStart)
    {
        _mapped = mapped;
        _native = native;
        _waits = waits;
        _functionStart = functionStart;
        Managed = IsManaged;
    }

    /// <summary>Every module a walk may unwind through (mapped and OS-loaded).</summary>
    public CodeRange[] Modules { get; init; } = [];

    /// <summary><see cref="IsManaged"/> as a delegate made once, for the capture's scan test.</summary>
    public Func<ulong, bool> Managed { get; }

    public bool IsMapped(ulong pc)
    {
        return In(pc, _mapped);
    }

    /// <summary>Neither mapped nor a native module: the loader's managed code (the walk ends at its first frame).</summary>
    public bool IsManaged(ulong pc)
    {
        return !In(pc, _mapped) && !In(pc, _native);
    }

    /// <summary>
    /// The sample of a stack (innermost first); <paramref name="site"/> is the game's return address above the frame
    /// the walk ended at, <paramref name="wait"/> the thread's loader wait. A thread is blocked when its innermost
    /// frame is a wait stub, in the game when it is mapped code, ours when a managed frame comes before any game frame,
    /// else in a system DLL.
    /// </summary>
    public CpuSample Classify(ReadOnlySpan<ulong> stack, ulong site, string? wait)
    {
        if (stack.IsEmpty)
        {
            return new CpuSample(CpuBucket.Unread, 0, 0, 0, null);
        }

        var (game, managed) = FirstGameAndManaged(stack);
        bool ours = managed >= 0 && (game < 0 || managed < game);
        ulong inner = stack[0];
        ulong function = 0;

        if (game >= 0 && !ours)
        {
            function = _functionStart(stack[game]);
        }

        if (!ours)
        {
            (site, wait) = (0, null);
        }
        else if (site == 0 && game >= 0)
        {
            // The walk got through our precompiled frames to the game: its first game frame is the return address.
            site = stack[game];
        }

        var (method, shim) = (0UL, 0UL);

        if (ours)
        {
            (method, shim) = ManagedFunctions(stack, game);
        }

        if (In(inner, _waits))
        {
            return new CpuSample(CpuBucket.Blocked, inner, function, site, wait, ours, method, shim);
        }

        if (game == 0)
        {
            return new CpuSample(CpuBucket.Game, 0, function, 0, null);
        }

        if (ours)
        {
            ulong native = 0;

            if (In(inner, _native))
            {
                native = inner;
            }

            return new CpuSample(CpuBucket.Ours, native, 0, site, wait, true, method, shim);
        }

        return new CpuSample(CpuBucket.System, inner, function, 0, null);
    }

    /// <summary>
    /// The starts of the innermost and the outermost managed function below the first game frame (the method running
    /// and the shim the game called); 0 for none. A frame without unwind data (a generated bridge) is passed over.
    /// </summary>
    private (ulong Method, ulong Shim) ManagedFunctions(ReadOnlySpan<ulong> stack, int game)
    {
        int end = stack.Length;
        ulong method = 0;
        ulong shim = 0;

        if (game >= 0)
        {
            end = game;
        }

        for (int i = 0; i < end; i++)
        {
            ulong start = 0;

            if (IsManaged(stack[i]))
            {
                start = _functionStart(stack[i]);
            }

            if (start == 0 || start == stack[i])
            {
                continue;
            }

            if (method == 0)
            {
                method = start;
            }

            shim = start;
        }

        return (method, shim);
    }

    /// <summary>The index of the first mapped frame and of the first managed one, -1 for none.</summary>
    private (int Game, int Managed) FirstGameAndManaged(ReadOnlySpan<ulong> stack)
    {
        int game = -1;
        int managed = -1;

        for (int i = 0; i < stack.Length; i++)
        {
            if (game < 0 && IsMapped(stack[i]))
            {
                game = i;
            }
            else if (managed < 0 && IsManaged(stack[i]))
            {
                managed = i;
            }
        }

        return (game, managed);
    }

    private static bool In(ulong pc, CodeRange[] ranges)
    {
        foreach (var range in ranges)
        {
            if (range.Contains(pc))
            {
                return true;
            }
        }

        return false;
    }
}
