// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// The desktop textures standing for console GPU address ranges, sorted by address. Lookups read the current array
/// without a lock; registering replaces it. A new texture retires every texture whose range it overlaps (the memory
/// was given to something else). A retired texture over the same range (another view of the same memory) keeps its
/// reference until disposal: descriptors the game wrote earlier may still name it. Any other retired texture is
/// released <see cref="GraceFrames"/> presents later, when the frames that could still sample it are done and the game
/// has rewritten its descriptors for the new contents. Before, every streamed-out texture stayed alive: a cutscene's
/// streaming grew the process by about 3 GB a minute once video memory spilled into system memory.
/// </summary>
internal sealed class GpuTextureRegistry : IDisposable
{
    /// <summary>Presents between retiring a texture whose memory was reused and releasing it.</summary>
    public const long GraceFrames = 120;

    private readonly object _gate = new();
    private readonly List<GpuTexture> _aliased = [];
    private readonly Queue<(GpuTexture Texture, long Frame)> _retired = new();
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
                return _aliased.Count + _retired.Count;
            }
        }
    }

    /// <summary>Retired textures released so far.</summary>
    public long Released => Interlocked.Read(ref _released);

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
            foreach (var entry in _sorted.Concat(_aliased).Concat(_retired.Select(retired => retired.Texture)))
            {
                ComObject.Release(entry.Desktop);
            }

            _aliased.Clear();
            _retired.Clear();
            _sorted = [];
        }
    }

    private void Retire(GpuTexture entry, GpuTexture replacement)
    {
        if (entry.Address == replacement.Address && entry.Size == replacement.Size)
        {
            _aliased.Add(entry);

            return;
        }

        _retired.Enqueue((entry, _frame));
    }
}
