// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.Input;

/// <summary>
/// Decides when the waiting block of an input script begins (<see cref="ScriptBlock"/>), from the frame signatures seen
/// while it waits: <see cref="RequiredMatches"/> in a row that show its learned screen, or for a <c>change</c> block that
/// differ from the first one seen; or its timeout. Not thread-safe: the watcher calls it under its lock.
/// </summary>
internal sealed class BlockMatcher
{
    public const int RequiredMatches = 2;

    /// <summary>Cells two frames in a row may differ by while a screen counts as still (<see cref="ScriptBlock.Settle"/>).</summary>
    public const int StillCells = 1;

    private int _block = -1;
    private ScreenSignature? _reference;
    private int _matches;
    private string? _shown;
    private ScreenSignature? _last;
    private double _stillSince;

    /// <summary>The nearest a frame came to the waiting screen block's screen since <see cref="ResetNearest"/>; int.MaxValue for none.</summary>
    public int Nearest { get; private set; } = int.MaxValue;

    /// <summary>The most cells a frame changed for the waiting <c>change</c> block since <see cref="ResetNearest"/>.</summary>
    public int Largest { get; private set; }

    /// <summary>
    /// A frame shown while <paramref name="block"/> waits: what began it, or null while it still waits.
    /// <paramref name="learned"/> and <paramref name="learnedLimit"/> are its learned screen (null for none). For a
    /// <c>change</c> block, the frames until the block before presses its first key (<paramref name="beforePress"/>) set
    /// the frame it compares against: the last of them, so a screen still settling when the block before began does not
    /// count as the change. With a settle time, what began it is returned once the frame stayed still that long after it
    /// showed (<paramref name="seconds"/> is the frame's time).
    /// </summary>
    public string? Seen(ScriptBlock block, int index, ScreenSignature signature, ScreenSignature? learned, int learnedLimit, bool beforePress, double seconds)
    {
        Follow(index);
        _shown ??= Shown(block, signature, learned, learnedLimit, beforePress);

        if (_shown is null || block.Settle <= 0)
        {
            return _shown;
        }

        return Settled(block, signature, seconds);
    }

    private string? Shown(ScriptBlock block, ScreenSignature signature, ScreenSignature? learned, int learnedLimit, bool beforePress)
    {
        if (block.Change)
        {
            return Changed(block, signature, beforePress);
        }

        if (learned is null)
        {
            return null;
        }

        int limit = learnedLimit;

        if (block.Limit > 0)
        {
            limit = block.Limit;
        }

        int distance = learned.Distance(signature, block.Region);
        Nearest = Math.Min(Nearest, distance);

        return Counted(distance <= limit, $"{distance} cells changed");
    }

    /// <summary>Whether <paramref name="block"/>, waiting since <paramref name="since"/>, has run past its timeout at <paramref name="seconds"/>.</summary>
    public static bool TimedOut(ScriptBlock block, double since, double seconds)
    {
        return block.Timeout > 0 && !double.IsNaN(since) && seconds - since >= block.Timeout;
    }

    public void ResetNearest()
    {
        Nearest = int.MaxValue;
        Largest = 0;
    }

    /// <summary>A block began: the next waiting block starts afresh.</summary>
    public void Began()
    {
        _block = -1;
    }

    private void Follow(int index)
    {
        if (index == _block)
        {
            return;
        }

        _block = index;
        _reference = null;
        _matches = 0;
        _shown = null;
        _last = null;
        ResetNearest();
    }

    /// <summary>Once the block's screen or change showed: what began it when the frame stayed still for the settle time.</summary>
    private string? Settled(ScriptBlock block, ScreenSignature signature, double seconds)
    {
        if (_last is null || _last.Distance(signature, block.SettleRegion) > StillCells)
        {
            _stillSince = seconds;
        }

        _last = signature;

        if (seconds - _stillSince < block.Settle)
        {
            return null;
        }

        return $"{_shown}, then still for {block.Settle:F1} s";
    }

    private string? Changed(ScriptBlock block, ScreenSignature signature, bool beforePress)
    {
        if (_reference is null || beforePress)
        {
            _reference = signature;

            return null;
        }

        int distance = _reference.Distance(signature, block.Region);
        Largest = Math.Max(Largest, distance);

        return Counted(distance >= Math.Max(1, block.Limit), $"{distance} cells changed since it began waiting");
    }

    private string? Counted(bool matched, string how)
    {
        if (!matched)
        {
            _matches = 0;

            return null;
        }

        _matches++;

        if (_matches < RequiredMatches)
        {
            return null;
        }

        return how;
    }
}
