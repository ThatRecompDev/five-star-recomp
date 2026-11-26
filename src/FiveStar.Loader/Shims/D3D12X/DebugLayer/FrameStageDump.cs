// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Text;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// <c>--dump-frame n --dump-frame-dir dir</c>: in the frame after present <c>n</c>, every render target, depth target and
/// UAV texture at least <see cref="MinimumWidth"/> wide is read back where a pass stops writing it (the list binds other
/// targets, moves it out of RENDER_TARGET, UNORDERED_ACCESS or DEPTH_WRITE, or closes), once per target and pass (the
/// last pipeline the list ran), at most <see cref="Limit"/>, and written as <c>NN-pass-WxH-fmtF.png</c> through
/// <see cref="StagePixels"/>. A UAV texture the game never moves out of UNORDERED_ACCESS is not seen. This class picks
/// what is read and names it; <see cref="DrawTrace"/> records the copies.
/// </summary>
internal sealed class FrameStageDump
{
    /// <summary>Readbacks in the dumped frame at most.</summary>
    public const int Limit = 64;

    /// <summary>Narrower targets (mip chains, probes, tiles) are not read.</summary>
    public const ulong MinimumWidth = 480;

    /// <summary>D3D12_RESOURCE_STATE_RENDER_TARGET, UNORDERED_ACCESS and DEPTH_WRITE: a pass writes a texture in these.</summary>
    public const int WriteStates = 0x4 | 0x8 | 0x10;

    private const int PassNameLength = 72;

    private readonly ConcurrentDictionary<(nint Resource, string Pass), byte> _taken = new();
    private readonly ConcurrentDictionary<nint, nint> _lists = new();
    private int _count;
    private bool _open;

    /// <summary>The present after which the frame is dumped (<c>--dump-frame n</c>); 0 dumps none.</summary>
    public static long Present { get; set; }

    /// <summary>Where the PNGs go (<c>--dump-frame-dir</c>).</summary>
    public static string Directory { get; set; } = "frame-dump";

    /// <summary>Whether the frame being recorded is the dumped one.</summary>
    public bool IsOpen => Volatile.Read(ref _open);

    /// <summary>After present <paramref name="present"/>: opens the dump for the next frame when it is the one asked for, else closes it.</summary>
    public void Begin(long present)
    {
        bool open = Present != 0 && present == Present;

        if (open)
        {
            _taken.Clear();
            _lists.Clear();
            Volatile.Write(ref _count, 0);
            System.IO.Directory.CreateDirectory(Directory);
        }

        Volatile.Write(ref _open, open);
    }

    /// <summary>The game list <paramref name="list"/> records on desktop list <paramref name="inner"/> (a barrier names only the desktop one).</summary>
    public void Seen(nint list, nint inner)
    {
        _lists[inner] = list;
    }

    /// <summary>The game list last seen recording on <paramref name="inner"/>; 0 for none.</summary>
    public nint GameList(nint inner)
    {
        return _lists.GetValueOrDefault(inner);
    }

    /// <summary>
    /// The PNG path for the first readback of <paramref name="resource"/> after <paramref name="pass"/>, with its order;
    /// null when the dump is closed, the target is narrow, already read after that pass, or the limit is reached.
    /// </summary>
    public string? Take(nint resource, string pass, ulong width, uint height, int format, bool depth, out int order)
    {
        order = 0;

        if (!IsOpen || width < MinimumWidth || !_taken.TryAdd((resource, pass), 0))
        {
            return null;
        }

        order = Interlocked.Increment(ref _count);

        if (order > Limit)
        {
            return null;
        }

        return Path.Combine(Directory, FileName(order, pass, width, height, format, depth));
    }

    /// <summary>"NN-pass-WxH-fmtF[-depth].png", the pass with every character but letters, digits, '.' and '-' as '_'.</summary>
    public static string FileName(int order, string pass, ulong width, uint height, int format, bool depth)
    {
        var name = new StringBuilder(pass.Length);

        foreach (char character in pass[..Math.Min(pass.Length, PassNameLength)])
        {
            bool kept = char.IsAsciiLetterOrDigit(character) || character is '.' or '-';
            char written = kept ? character : '_';
            name.Append(written);
        }

        string kind = depth ? "-depth" : string.Empty;

        return $"{order:D2}-{name}-{width}x{height}-fmt{format}{kind}.png";
    }
}
