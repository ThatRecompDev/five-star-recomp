// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using FiveStar.Cli.Archive;
using FiveStar.Cli.Screens;
using FiveStar.Formats.Images;

namespace FiveStar.Cli;

/// <summary>
/// The <c>fivestar screen</c> commands: learn an input-script screen from a captured present (<c>present-N.png</c> of
/// <c>--capture-dir</c>) without pressing F8 in a boot, and check another capture against it.
/// </summary>
internal static class ScreenCommands
{
    private const int Success = 0;
    private const int NoMatch = 1;
    private const int UsageError = 2;
    private const int Positionals = 2;
    private const int RegionFields = 4;
    private const int LastColumnField = 2;
    private const int LastRowField = 3;

    private static readonly string[] Flags = [];

    public static int Run(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            PrintHelp(Console.Error);

            return UsageError;
        }

        var arguments = new CommandArguments(args.Skip(1).ToList(), Flags);

        switch (args[0])
        {
            case "learn":
                return RunLearn(arguments);
            case "match":
                return RunMatch(arguments);
            case "help":
                PrintHelp(Console.Out);

                return Success;
            default:
                PrintHelp(Console.Error);

                return UsageError;
        }
    }

    private static int RunLearn(CommandArguments arguments)
    {
        string? screens = arguments.Option("--screens");

        if (arguments.Positionals.Count != Positionals || screens is null || !File.Exists(arguments.Positionals[0]))
        {
            Console.Error.WriteLine("usage: fivestar screen learn <png> <name> --screens <file> [--limit <cells>]");

            return UsageError;
        }

        var image = PngReader.Read(arguments.Positionals[0]);
        var cells = ScreenCells.Of(image.Pixels, image.Width, image.Height);
        string name = arguments.Positionals[1];
        int? limit = null;

        if (arguments.Option("--limit") is not null)
        {
            limit = (int)arguments.LongOption("--limit", ScreenCells.DefaultMaxChanged);
        }

        var lines = File.Exists(screens) ? File.ReadAllLines(screens) : [];
        File.WriteAllLines(screens, ScreenFile.Learn(lines, name, cells, limit));
        Console.Out.WriteLine($"{name}: learned from {arguments.Positionals[0]} ({image.Width}x{image.Height}) into {screens}");

        return Success;
    }

    private static int RunMatch(CommandArguments arguments)
    {
        string? screens = arguments.Option("--screens");

        if (arguments.Positionals.Count != Positionals || screens is null || !File.Exists(screens) || !File.Exists(arguments.Positionals[0]))
        {
            Console.Error.WriteLine("usage: fivestar screen match <png> <name> --screens <file> [--region <c0,r0,c1,r1>] [--limit <cells>]");

            return UsageError;
        }

        string name = arguments.Positionals[1];

        if (!ScreenFile.TryFind(File.ReadLines(screens), name, out var learned, out int limit))
        {
            Console.Error.WriteLine($"{screens} holds no screen {name}");

            return UsageError;
        }

        var image = PngReader.Read(arguments.Positionals[0]);
        var cells = ScreenCells.Of(image.Pixels, image.Width, image.Height);
        int[] region = Region(arguments.Option("--region"));
        limit = (int)arguments.LongOption("--limit", limit);

        int whole = ScreenCells.Distance(cells, learned, 0, 0, ScreenCells.Columns - 1, ScreenCells.Rows - 1);
        int changed = ScreenCells.Distance(cells, learned, region[0], region[1], region[LastColumnField], region[LastRowField]);
        bool matches = changed <= limit;
        string verdict = matches ? "matches" : "does not match";

        Console.Out.WriteLine($"{arguments.Positionals[0]}: {changed} cells of region {string.Join(',', region)} differ from {name} (limit {limit}; {whole} of the whole frame): {verdict}");

        if (matches)
        {
            return Success;
        }

        return NoMatch;
    }

    /// <summary><c>--region c0,r0,c1,r1</c>, or the whole frame.</summary>
    private static int[] Region(string? text)
    {
        if (text is null)
        {
            return [0, 0, ScreenCells.Columns - 1, ScreenCells.Rows - 1];
        }

        int[] region = [.. text.Split(',').Select(field => int.Parse(field, CultureInfo.InvariantCulture))];
        bool inside = region.Length == RegionFields && region[0] >= 0 && region[1] >= 0 && region[0] <= region[LastColumnField] && region[1] <= region[LastRowField];

        if (!inside || region[LastColumnField] >= ScreenCells.Columns || region[LastRowField] >= ScreenCells.Rows)
        {
            throw new FormatException($"--region {text}: four cells c0,r0,c1,r1 inside {ScreenCells.Columns}x{ScreenCells.Rows}");
        }

        return region;
    }

    private static void PrintHelp(TextWriter output)
    {
        output.WriteLine("fivestar screen <command>");
        output.WriteLine("  learn <png> <name> --screens <file> [--limit <cells>]  stores a captured present's signature as an input-script");
        output.WriteLine("                                        screen (48x27 cells of luminance, as F8 or a learn line would), replacing");
        output.WriteLine("                                        the screen's line or adding one; the limit defaults to the line's or 40");

        output.WriteLine("  match <png> <name> --screens <file> [--region <c0,r0,c1,r1>] [--limit <cells>]");
        output.WriteLine("                                        how many cells of another capture differ from a learned screen (in the");
        output.WriteLine("                                        region, as a screen block's region compares), and whether that is within");
        output.WriteLine("                                        the limit; exit code 1 when it is not");
    }
}
