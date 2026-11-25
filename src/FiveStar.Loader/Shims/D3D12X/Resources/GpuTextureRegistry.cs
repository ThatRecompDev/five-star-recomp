// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// The desktop textures standing for console GPU address ranges, sorted by address. Lookups read the current array
/// without a lock; registering replaces it. A new texture retires every texture whose range it overlaps (the memory
/// was given to something else). The newest <see cref="KeptAliases"/> retired textures over exactly one range (other
/// views of the same memory) keep their reference until disposal, as descriptors the game wrote earlier may still name
/// them; older ones are released <see cref="AliasGraceFrames"/> presents later. Any other retired texture is
/// released <see cref="GraceFrames"/> presents later, when the frames that could still sample it are done and the game
/// has rewritten its descriptors for the new contents. Before, every streamed-out texture stayed alive: a cutscene's
/// streaming grew the process by about 3 GB a minute once video memory spilled into system memory.
/// </summary>
internal sealed class GpuTextureRegistry : IDisposable
{
    /// <summary>Presents between retiring a texture whose memory was reused and releasing it.</summary>
    public const long GraceFrames = 120;

    /// <summary>Retired textures over exactly one range kept until disposal: the newest ones.</summary>
    public const int KeptAliases = 2;

    /// <summary>Presents before an older texture over the same range is released.</summary>
    public const long AliasGraceFrames = 600;

    private readonly object _gate = new();
    private readonly Dictionary<(ulong Address, ulong Size), Queue<GpuTexture>> _aliases = [];
    private readonly Queue<(GpuTexture Texture, long Frame)> _retired = new();
    private readonly Queue<(GpuTexture Texture, long Frame)> _agedAliases = new();
    private int _aliasCount;
    private GpuTexture[] _sorted = [];
    private long _frame;
    private long _released;

    public int Count => Volatile.Read(ref _sorted).Length;

    /// <summary>Retired textures still holding their reference.</summary>
    public int RetiredCount
    {
        get
        {
            lock (_gate)
            {
                return _aliasCount + _retired.Count + _agedAliases.Count;
            }
        }
    }

    /// <summary>Retired textures released so far.</summary>
    public long Released => Interlocked.Read(ref _released);

    /// <summary>"N textures, retired R (aliased A), released N", for the texture counters.</summary>
    public string Summary
    {
        get
        {
            lock (_gate)
            {
                int retired = _aliasCount + _retired.Count + _agedAliases.Count;

                return $"{_sorted.Length} textures, retired {retired} (aliased {_aliasCount + _agedAliases.Count}), released {_released}";
            }
        }
    }

    public void Register(GpuTexture texture)
    {
        lock (_gate)
        {
            var kept = new List<GpuTexture>(_sorted.Length + 1);

            foreach (var entry in _sorted)
            {
                if (entry.Address < texture.Address + texture.Size && texture.Address < entry.Address + entry.Size)
                {
                    Retire(entry, texture);
                }
                else
                {
                    kept.Add(entry);
                }
            }

            kept.Add(texture);
            kept.Sort((left, right) => left.Address.CompareTo(right.Address));
            Volatile.Write(ref _sorted, kept.ToArray());
        }
    }

    /// <summary>Once per present: releases the retired textures whose grace period ended.</summary>
    public void Advance(long frame)
    {
        lock (_gate)
        {
            _frame = frame;

            while (_retired.Count > 0 && _retired.Peek().Frame + GraceFrames <= frame)
            {
                ComObject.Release(_retired.Dequeue().Texture.Desktop);
                _released++;
            }

            while (_agedAliases.Count > 0 && _agedAliases.Peek().Frame + AliasGraceFrames <= frame)
            {
                ComObject.Release(_agedAliases.Dequeue().Texture.Desktop);
                _released++;
            }
        }
    }

    /// <summary>The texture whose range holds <paramref name="address"/>, or null.</summary>
    [HotPath]
    public GpuTexture? Find(ulong address)
    {
        var sorted = Volatile.Read(ref _sorted);
        int low = 0;
        int high = sorted.Length - 1;

        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            var entry = sorted[middle];

            if (entry.Contains(address))
            {
                return entry;
            }

            if (address < entry.Address)
            {
                high = middle - 1;
            }
            else
            {
                low = middle + 1;
            }
        }

        return null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            var aliases = _aliases.Values.SelectMany(queue => queue);
            var retired = _retired.Concat(_agedAliases).Select(entry => entry.Texture);

            foreach (var entry in _sorted.Concat(aliases).Concat(retired))
            {
                ComObject.Release(entry.Desktop);
            }

            _aliases.Clear();
            _aliasCount = 0;
            _retired.Clear();
            _agedAliases.Clear();
            _sorted = [];
        }
    }

    private void Retire(GpuTexture entry, GpuTexture replacement)
    {
        if (entry.Address == replacement.Address && entry.Size == replacement.Size)
        {
            Alias(entry);

            return;
        }

        _retired.Enqueue((entry, _frame));
    }

    /// <summary>
    /// Keeps a texture retired by another over exactly its range; past <see cref="KeptAliases"/> for that range the
    /// oldest is released <see cref="AliasGraceFrames"/> presents later. All were kept before, and a streaming pool
    /// reuses fixed-size slots: the growth after the story load followed the textures made.
    /// </summary>
    private void Alias(GpuTexture entry)
    {
        var range = (entry.Address, entry.Size);

        if (!_aliases.TryGetValue(range, out var kept))
        {
            kept = new Queue<GpuTexture>();
            _aliases[range] = kept;
        }

        kept.Enqueue(entry);
        _aliasCount++;

        if (kept.Count > KeptAliases)
        {
            _agedAliases.Enqueue((kept.Dequeue(), _frame));
            _aliasCount--;
        }
    }
}
