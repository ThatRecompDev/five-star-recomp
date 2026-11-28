// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Text;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// <c>--dump-frame n --dump-frame-dir dir</c>: in the frame after present <c>n</c>, every render target, depth target and
/// UAV texture at least <see cref="MinimumWidth"/> wide (480, <c>--dump-frame-min-width</c>) is read back where a pass stops writing it (the list binds other
/// targets, moves it out of RENDER_TARGET, UNORDERED_ACCESS or DEPTH_WRITE, or closes), once per target and pass (the
/// last pipeline the list ran; with <see cref="Filter"/> only passes whose name holds one of its texts), and written as
/// <c>NN-pass-WxH-fmtF.png</c> through <see cref="StagePixels"/>, at most <see cref="Max"/> PNGs. When the readback
/// buffers' cap refuses one, that frame takes nothing more and the dump goes on in a later frame (a batch, once the
/// earlier batch was reported) from the targets and passes not taken yet. A UAV texture the game never moves out of
/// UNORDERED_ACCESS is not seen. This class picks what is read and names it; <see cref="DrawTrace"/> records the copies.
/// </summary>
internal sealed class FrameStageDump
{
    /// <summary>PNGs written by default (<c>--dump-frame-max</c>).</summary>
    public const int DefaultMax = 160;

    /// <summary>Narrower targets (mip chains, probes, tiles) are not read by default.</summary>
    public const ulong DefaultMinimumWidth = 480;

    /// <summary>D3D12_RESOURCE_STATE_RENDER_TARGET, UNORDERED_ACCESS and DEPTH_WRITE: a pass writes a texture in these.</summary>
    public const int WriteStates = 0x4 | 0x8 | 0x10;

    private const int PassNameLength = 72;

    /// <summary><c>--dump-frame world</c>: presents after the first world present (its first frames still stream textures).</summary>
    public const long DefaultWorldOffset = 300;
    private const string WorldWord = "world";
    private const string SecondsSuffix = "s";

    /// <summary>DXGI R32G8X24_TYPELESS, D32_FLOAT_S8X24_UINT, R24G8_TYPELESS and D24_UNORM_S8_UINT.</summary>
    private const int R32G8X24Typeless = 19;
    private const int D32S8 = 20;
    private const int R24G8Typeless = 44;
    private const int D24S8 = 45;

    private readonly ConcurrentDictionary<(nint Resource, string Pass), byte> _taken = new();
    private readonly ConcurrentDictionary<nint, nint> _lists = new();
    private int _count;
    private int _batchLimit;
    private bool _open;
    private bool _refused;
    private bool _active;

    /// <summary>The present after which the frame is dumped (<c>--dump-frame n</c>); 0 dumps none.</summary>
    public static long Present { get; set; }

    /// <summary>
    /// <c>--dump-frame world+n</c>: <see cref="Present"/> is set n presents after the first world present (the draw
    /// trace's world trigger), so the dump does not depend on how long the load takes; -1 when a present was given.
    /// </summary>
    public static long WorldOffset { get; set; } = -1;

    /// <summary>
    /// <c>--dump-frame Ns</c>: <see cref="Present"/> is set to the first present N seconds after the loader started, so a
    /// timed scene (the intro's credits run on the clock, not on presents) is dumped whatever the frame rate; -1 for none.
    /// </summary>
    public static long AfterSeconds { get; set; } = -1;

    /// <summary>The <c>--exit-when-done</c> entry of the dump.</summary>
    public const string EntryName = "dump-frame";

    /// <summary>Where the PNGs go (<c>--dump-frame-dir</c>).</summary>
    public static string Directory { get; set; } = "frame-dump";

    /// <summary>PNGs written at most (<c>--dump-frame-max n</c>).</summary>
    public static int Max { get; set; } = DefaultMax;

    /// <summary>Narrower targets are not read (<c>--dump-frame-min-width n</c>; 80 takes the tiled lighting's tile masks).</summary>
    public static ulong MinimumWidth { get; set; } = DefaultMinimumWidth;

    /// <summary>Comma-separated texts one of which a pass name must hold (<c>--dump-frame-filter</c>); null takes every pass.</summary>
    public static string? Filter { get; set; }

    /// <summary>Whether the frame being recorded is dumped (the first batch or a later one).</summary>
    public bool IsOpen => Volatile.Read(ref _open);

    /// <summary>The batch being recorded, 1 for the frame after <see cref="Present"/>.</summary>
    public int Batch { get; private set; }

    /// <summary>
    /// After present <paramref name="present"/>: opens the first batch after <see cref="Present"/>, or a later one when the
    /// last was refused, its probes were reported (<paramref name="reportPending"/> false) and fewer than <see cref="Max"/>
    /// PNGs are <paramref name="written"/>; else closes. Returns what to log when the dump opens or ends.
    /// </summary>
    public string? Begin(long present, bool reportPending, int written)
    {
        bool wasOpen = IsOpen;
        Volatile.Write(ref _open, false);

        if (Present != 0 && present == Present)
        {
            _taken.Clear();
            _lists.Clear();
            _count = 0;
            _active = true;
            Batch = 0;
            System.IO.Directory.CreateDirectory(Directory);

            return Open(written);
        }

        if (!_active || wasOpen || reportPending)
        {
            return null;
        }

        if (!_refused || written >= Max)
        {
            _active = false;
            FiveStar.Loader.Diagnostics.RunEnd.Fired(EntryName);

            return $"  [dump-frame] done after {Batch} batches: {written} PNGs in {Directory}";
        }

        return Open(written);
    }

    private string Open(int written)
    {
        Batch++;
        _refused = false;
        _batchLimit = Max - written;
        Volatile.Write(ref _open, true);

        return $"  [dump-frame] batch {Batch}: up to {_batchLimit} more targets into {Directory}";
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
    /// null when the batch is closed or refused, the target is narrow, the pass filtered out or already taken, or the
    /// batch's share of <see cref="Max"/> is used.
    /// </summary>
    public string? Take(nint resource, string pass, ulong width, uint height, int format, bool depth, out int order)
    {
        order = 0;

        if (!IsOpen || Volatile.Read(ref _refused) || width < MinimumWidth || !Passes(pass, Filter) || _taken.ContainsKey((resource, pass)))
        {
            return null;
        }

        if (Volatile.Read(ref _batchLimit) <= 0 || !_taken.TryAdd((resource, pass), 0))
        {
            return null;
        }

        Interlocked.Decrement(ref _batchLimit);
        order = Interlocked.Increment(ref _count);

        return Path.Combine(Directory, FileName(order, pass, width, height, format, depth));
    }

    /// <summary>The readback cap refused the copy just taken: it is left for the next batch, and this batch takes nothing more.</summary>
    public void Refused(nint resource, string pass)
    {
        _taken.TryRemove((resource, pass), out _);
        Volatile.Write(ref _refused, true);
    }

    /// <summary>Whether <paramref name="pass"/> holds one of the comma-separated texts of <paramref name="filter"/> (null: any pass).</summary>
    public static bool Passes(string pass, string? filter)
    {
        return filter is null || DrawTrace.MatchesFilter(pass, filter);
    }

    /// <summary>
    /// <c>--dump-frame</c>'s value: a present number, or <c>world</c> / <c>world+n</c> (n presents after the first world
    /// present, <see cref="DefaultWorldOffset"/> without one). False for anything else.
    /// </summary>
    public static bool TryParseWhen(string text, out long present, out long worldOffset)
    {
        present = 0;
        worldOffset = -1;

        if (!text.StartsWith(WorldWord, StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out present) && present > 0;
        }

        string rest = text[WorldWord.Length..];
        worldOffset = DefaultWorldOffset;

        if (rest.Length == 0)
        {
            return true;
        }

        return rest[0] == '+' && long.TryParse(rest[1..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out worldOffset) && worldOffset > 0;
    }

    /// <summary><c>--dump-frame</c>'s value as seconds after the loader started ("350s"); false for anything else.</summary>
    public static bool TryParseSeconds(string text, out long seconds)
    {
        seconds = -1;

        if (!text.EndsWith(SecondsSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return long.TryParse(text[..^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out seconds) && seconds > 0;
    }

    /// <summary>Whether a depth target of <paramref name="format"/> has a stencil plane (R32G8X24, D32S8, R24G8, D24S8), dumped beside its depth.</summary>
    public static bool HasStencil(int format)
    {
        return format is R32G8X24Typeless or D32S8 or R24G8Typeless or D24S8;
    }

    /// <summary>"x-depth.png" as "x-stencil.png".</summary>
    public static string StencilPath(string png)
    {
        return Path.ChangeExtension(png, null).Replace("-depth", string.Empty, StringComparison.Ordinal) + "-stencil.png";
    }

    /// <summary>"NNN-pass-WxH-fmtF[-depth].png", the pass with every character but letters, digits, '.' and '-' as '_'.</summary>
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

        return $"{order:D3}-{name}-{width}x{height}-fmt{format}{kind}.png";
    }
}
