// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Shaders.Translation.Core;

/// <summary>
/// The registers each block reads before writing them and those it writes, as its translation reads and writes them.
/// A register is a hardware input (user data, a system value) when some path from the entry reads it before writing it
/// (<see cref="ReadBeforeWritten"/>); a read before any write in translation order finds only some of them: a register
/// one path writes and the join after it reads keeps its input on the other path.
/// </summary>
internal sealed class RegisterAccesses
{
    private readonly Dictionary<long, BlockAccess> _blocks = [];
    private BlockAccess? _current;

    /// <summary>Starts recording the block at <paramref name="block"/>.</summary>
    public void Begin(long block)
    {
        _current = new BlockAccess();
        _blocks[block] = _current;
    }

    /// <summary>A read of <paramref name="key"/> in the current block.</summary>
    public void Read(RegisterKey key)
    {
        if (_current is not null && !_current.Writes.Contains(key))
        {
            _current.Reads.Add(key);
        }
    }

    /// <summary>A write of <paramref name="key"/> in the current block.</summary>
    public void Write(RegisterKey key)
    {
        _current?.Writes.Add(key);
    }

    /// <summary>
    /// The registers some path from <paramref name="entry"/> reads before writing them (backward liveness to a fixed point
    /// over the blocks recorded and their <paramref name="successors"/>).
    /// </summary>
    public IReadOnlySet<RegisterKey> ReadBeforeWritten(long entry, IReadOnlyDictionary<long, List<long>> successors)
    {
        var live = _blocks.Keys.ToDictionary(block => block, _ => new HashSet<RegisterKey>());
        var changed = true;

        while (changed)
        {
            changed = false;

            foreach (var (block, access) in _blocks)
            {
                var into = new HashSet<RegisterKey>(access.Reads);

                foreach (var successor in successors.GetValueOrDefault(block) ?? [])
                {
                    if (live.TryGetValue(successor, out var after))
                    {
                        into.UnionWith(after.Where(key => !access.Writes.Contains(key)));
                    }
                }

                changed |= into.Count != live[block].Count;
                live[block] = into;
            }
        }

        return live.GetValueOrDefault(entry) ?? [];
    }

    /// <summary>What one block reads before writing and what it writes.</summary>
    private sealed class BlockAccess
    {
        public HashSet<RegisterKey> Reads { get; } = [];

        public HashSet<RegisterKey> Writes { get; } = [];
    }
}
