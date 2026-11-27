// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace FiveStar.Shaders.Compilation;

/// <summary>
/// Hand-written programs that replace translations (<c>--shader-overrides &lt;dir&gt;</c>): <c>&lt;name&gt;.hlsl</c>,
/// compiled as the translation would be, or <c>&lt;name&gt;.dxil</c>, used as it is, by the extracted program's name.
/// A replacement keeps its translation's interface (docs/shader-translation.md#replacing-a-translation). Its files are
/// named by <see cref="Stem"/>, which holds the replacement's content hash, so a cache never mixes the two.
/// </summary>
public sealed class ShaderOverrides
{
    public const string HlslExtension = ".hlsl";
    public const string DxilExtension = ".dxil";

    /// <summary>The infix of a replacement's files in the program cache: <c>&lt;name&gt;.override-&lt;hash&gt;</c>.</summary>
    public const string StemInfix = ".override-";

    /// <summary>The start of the header <see cref="StartFrom"/> writes.</summary>
    public const string HeaderStart = "// Five Star shader override of ";

    private const int HashDigits = 16;

    private readonly Dictionary<string, Entry> _entries;

    private ShaderOverrides(string folder, Dictionary<string, Entry> entries)
    {
        Folder = folder;
        _entries = entries;
    }

    /// <summary>The folder the replacements were read from.</summary>
    public string Folder { get; }

    /// <summary>The names of the replaced programs.</summary>
    public IReadOnlyCollection<string> Names => _entries.Keys;

    /// <summary>
    /// The replacements in <paramref name="folder"/>, read once: a later edit takes a new boot. An HLSL file wins over
    /// a DXIL file of the same name.
    /// </summary>
    public static ShaderOverrides Open(string folder)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(folder, "*" + DxilExtension).Concat(Directory.EnumerateFiles(folder, "*" + HlslExtension)))
        {
            var bytes = File.ReadAllBytes(path);
            var dxil = path.EndsWith(DxilExtension, StringComparison.OrdinalIgnoreCase);
            string? hlsl = null;

            if (!dxil)
            {
                hlsl = Encoding.UTF8.GetString(bytes);
            }

            entries[Path.GetFileNameWithoutExtension(path)] = new Entry(path, hlsl, ContentHash.Of(bytes)[..HashDigits]);
        }

        return new ShaderOverrides(folder, entries);
    }

    /// <summary>Whether <paramref name="name"/> has a replacement.</summary>
    public bool Has(string name)
    {
        return _entries.ContainsKey(name);
    }

    /// <summary>The replacement's HLSL, or null when it has none or is DXIL.</summary>
    public string? Hlsl(string name)
    {
        return _entries.GetValueOrDefault(name)?.Hlsl;
    }

    /// <summary>The replacement's DXIL file, or null when it has none or is HLSL.</summary>
    public string? DxilFile(string name)
    {
        if (!_entries.TryGetValue(name, out var entry) || entry.Hlsl is not null)
        {
            return null;
        }

        return entry.Path;
    }

    /// <summary>
    /// The file stem of the program in a cache: <c>&lt;name&gt;.override-&lt;content hash&gt;</c> for a replaced one, so
    /// the translation's files and every edit of the replacement keep their own; the name for any other.
    /// </summary>
    public string Stem(string name)
    {
        if (!_entries.TryGetValue(name, out var entry))
        {
            return name;
        }

        return name + StemInfix + entry.Hash;
    }

    /// <summary>
    /// The text of a replacement that starts as the translation <paramref name="hlsl"/>: a header naming the program and
    /// the translation's hash, which tells a replacement whose translation has changed since.
    /// </summary>
    public static string StartFrom(string name, string hlsl)
    {
        var hash = ContentHash.OfText(hlsl)[..HashDigits];

        return $"{HeaderStart}{name}\n// from translation {hash}: keep its root signature, user data, descriptor tables and includes\n{hlsl}";
    }

    private sealed record Entry(string Path, string? Hlsl, string Hash);
}
