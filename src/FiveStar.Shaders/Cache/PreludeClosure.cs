// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using FiveStar.Shaders.Compilation;
using FiveStar.Shaders.Translation;

namespace FiveStar.Shaders.Cache;

/// <summary>
/// The part of the runtime headers a program's compile depends on (<see cref="ProgramContentStore"/>): every header the
/// HLSL includes, split into top-level pieces. A function definition is a piece of its own (also inside a conditional
/// block, whose directive lines stay with the rest); everything else (defines, directives, structs, statics, globals,
/// declarations) is kept as one text without comments and blank lines. A program depends on all of the latter and on
/// the functions its own text and the kept text reach by name, transitively (every overload of a name). A change to a function
/// a program does not reach leaves its key, and so its compiled DXIL, alone (the DXIL backend and dxc both compile it
/// to the same bytes): the whole headers were in the key before, and each prelude change compiled all 25,884 programs
/// again. Conservative where it cannot tell: a name in a block comment or a string still reaches.
/// </summary>
public static partial class PreludeClosure
{
    private static readonly ConcurrentDictionary<string, Header> Headers = new(StringComparer.Ordinal);

    /// <summary>The hash of what <paramref name="hlsl"/>'s compile reads from the runtime headers it includes.</summary>
    public static string Of(string hlsl)
    {
        return Of(hlsl, Embedded);
    }

    /// <summary>As <see cref="Of(string)"/>, with the headers <paramref name="headers"/> names (null: not a runtime header).</summary>
    internal static string Of(string hlsl, Func<string, Header?> headers)
    {
        var included = new List<Header>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Include(hlsl, included, seen, headers);

        if (included.Count == 0)
        {
            return string.Empty;
        }

        var names = Identifiers(hlsl);
        var parts = new List<string>();

        foreach (var header in included)
        {
            parts.Add(header.Name);
            parts.Add(header.Always);
            names.UnionWith(Identifiers(header.Always));
        }

        var functions = Reached(included, names);

        foreach (var (header, name, text) in functions)
        {
            parts.Add($"{header}:{name}");
            parts.Add(text);
        }

        return ContentHash.OfText([.. parts]);
    }

    /// <summary>The headers <paramref name="text"/> includes, and theirs, each once, in order.</summary>
    private static void Include(string text, List<Header> included, HashSet<string> seen, Func<string, Header?> headers)
    {
        foreach (Match include in IncludePattern().Matches(text))
        {
            var name = include.Groups["name"].Value;

            if (!seen.Add(name) || headers(name) is not Header header)
            {
                continue;
            }

            included.Add(header);
            Include(header.Text, included, seen, headers);
        }
    }

    /// <summary>The functions the names reach, transitively, in header and definition order.</summary>
    private static List<(string Header, string Name, string Text)> Reached(List<Header> headers, HashSet<string> names)
    {
        var reached = new HashSet<(string Header, int Index)>();
        var pending = new Queue<string>(names);

        while (pending.Count > 0)
        {
            var name = pending.Dequeue();

            foreach (var header in headers)
            {
                if (!header.Functions.TryGetValue(name, out var indices))
                {
                    continue;
                }

                foreach (int index in indices)
                {
                    if (!reached.Add((header.Name, index)))
                    {
                        continue;
                    }

                    foreach (var called in Identifiers(header.Pieces[index].Text))
                    {
                        if (names.Add(called))
                        {
                            pending.Enqueue(called);
                        }
                    }
                }
            }
        }

        var byName = headers.ToDictionary(header => header.Name, StringComparer.Ordinal);

        return [.. reached.OrderBy(entry => entry.Header, StringComparer.Ordinal).ThenBy(entry => entry.Index)
            .Select(entry => (entry.Header, byName[entry.Header].Pieces[entry.Index].Name!, byName[entry.Header].Pieces[entry.Index].Text))];
    }

    /// <summary>A runtime header, parsed once.</summary>
    private static Header? Embedded(string name)
    {
        if (!RuntimeIncludes.Names.Contains(name))
        {
            return null;
        }

        return Headers.GetOrAdd(name, static key => Header.Parse(key, RuntimeIncludes.Read(key)));
    }

    private static HashSet<string> Identifiers(string text)
    {
        return [.. IdentifierPattern().Matches(LineCommentPattern().Replace(text, string.Empty)).Select(match => match.Value)];
    }

    [GeneratedRegex(@"^[ \t]*#[ \t]*include[ \t]+""(?<name>[^""]+)""", RegexOptions.Multiline)]
    private static partial Regex IncludePattern();

    /// <summary>A line comment (no string in the headers holds "//").</summary>
    [GeneratedRegex(@"//[^\n]*")]
    private static partial Regex LineCommentPattern();

    [GeneratedRegex(@"[A-Za-z_]\w*")]
    private static partial Regex IdentifierPattern();

    /// <summary>A function definition's head at the top level: a type, the name, its parameters' opening.</summary>
    [GeneratedRegex(@"^[A-Za-z_]\w*(?:[ \t]+[A-Za-z_]\w*)*[ \t]+(?<name>[A-Za-z_]\w*)[ \t]*\(")]
    private static partial Regex FunctionHeadPattern();

    /// <summary>One top-level piece of a header: a function (with its name) or anything else (no name).</summary>
    internal readonly record struct Piece(string? Name, string Text);

    /// <summary>A header split into pieces, with the indices of each function name's definitions.</summary>
    internal sealed class Header
    {
        private Header(string name, string text, List<Piece> pieces)
        {
            Name = name;
            Text = text;
            Pieces = pieces;
            Always = Code(pieces.Where(piece => piece.Name is null).Select(piece => piece.Text));

            for (int i = 0; i < pieces.Count; i++)
            {
                if (pieces[i].Name is string function)
                {
                    if (!Functions.TryGetValue(function, out var list))
                    {
                        list = [];
                        Functions[function] = list;
                    }

                    list.Add(i);
                }
            }
        }

        public string Name { get; }

        public string Text { get; }

        public List<Piece> Pieces { get; }

        /// <summary>What every includer depends on: all but the functions, without comments and blank lines.</summary>
        public string Always { get; }

        public Dictionary<string, List<int>> Functions { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Splits <paramref name="text"/> line by line: a line at brace depth 0 (preprocessor blocks or not)
        /// that heads a function (a type, a name, "(", and a body before any ";") starts a function piece, which ends
        /// where its braces close; every other line goes to the running non-function piece.
        /// </summary>
        public static Header Parse(string name, string text)
        {
            var pieces = new List<Piece>();
            var other = new StringBuilder();
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                var head = FunctionHeadPattern().Match(lines[i]);

                if (head.Success && Defines(lines, i, out int end))
                {
                    Flush(pieces, other);
                    pieces.Add(new Piece(head.Groups["name"].Value, string.Join('\n', lines[i..(end + 1)])));
                    i = end;

                    continue;
                }

                other.Append(lines[i]).Append('\n');
            }

            Flush(pieces, other);

            // A function piece that holds another function's head at column 0 was cut wrong (braces split across
            // preprocessor branches): the whole header is then one piece every includer depends on.
            if (pieces.Any(piece => piece.Name is not null && piece.Text.Split('\n').Skip(1).Any(line => FunctionHeadPattern().IsMatch(line))))
            {
                pieces = [new Piece(null, text)];
            }

            return new Header(name, text, pieces);
        }

        /// <summary>
        /// Whether the function head at <paramref name="start"/> has a body (a "{" before any ";"); <paramref name="end"/>
        /// is the line its braces close on. A head whose braces do not close is not a function piece.
        /// </summary>
        private static bool Defines(string[] lines, int start, out int end)
        {
            int depth = 0;
            bool opened = false;
            end = start;

            for (int i = start; i < lines.Length; i++)
            {
                foreach (char c in lines[i])
                {
                    if (c == ';' && !opened)
                    {
                        return false;
                    }

                    if (c == '{')
                    {
                        depth++;
                        opened = true;
                    }
                    else if (c == '}')
                    {
                        depth--;
                    }
                }

                if (opened && depth == 0)
                {
                    end = i;

                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The non-function pieces as one text: line comments and blank lines dropped, which a compile does not read, so a
        /// function added between two pieces or a comment edited leaves it as it was.
        /// </summary>
        private static string Code(IEnumerable<string> pieces)
        {
            var code = new StringBuilder();

            foreach (var line in string.Concat(pieces).Split('\n'))
            {
                var kept = LineCommentPattern().Replace(line, string.Empty).TrimEnd();

                if (kept.Length > 0)
                {
                    code.Append(kept).Append('\n');
                }
            }

            return code.ToString();
        }

        private static void Flush(List<Piece> pieces, StringBuilder other)
        {
            if (other.Length > 0)
            {
                pieces.Add(new Piece(null, other.ToString()));
                other.Clear();
            }
        }
    }
}
