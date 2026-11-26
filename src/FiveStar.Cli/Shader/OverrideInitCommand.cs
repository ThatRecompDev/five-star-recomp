// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Compilation;
using FiveStar.Shaders.Disassembly;

namespace FiveStar.Cli.Shader;

/// <summary>
/// <c>fivestar shader override-init &lt;program&gt; &lt;dir&gt;</c>: writes the program's translation, as the loader makes
/// it on demand with the default choices, to <c>&lt;dir&gt;/&lt;program&gt;.hlsl</c>, the start of a hand-written
/// replacement (<c>fivestar-loader --shader-overrides &lt;dir&gt;</c>).
/// </summary>
internal static class OverrideInitCommand
{
    private const int Success = 0;
    private const int Failure = 1;
    private const int TwoArguments = 2;
    private const string ProgramFolderVariable = "FIVESTAR_SHADER_DIR";
    private const string GroupWaveFlag = "group-wave";
    private const string ForceFlag = "force";

    public static int Run(IReadOnlyList<string> args)
    {
        var arguments = new CommandArguments(args, new HashSet<string> { GroupWaveFlag, ForceFlag });

        if (arguments.Positional.Count != TwoArguments)
        {
            throw new ArgumentException("override-init <program> <dir> [--programs <fssm folder>] [--group-wave] [--force]");
        }

        var (name, folder) = (arguments.Positional[0], arguments.Positional[1]);
        var programs = arguments.Option("programs") ?? Environment.GetEnvironmentVariable(ProgramFolderVariable) ?? throw new ArgumentException("no FSSM folder: --programs or FIVESTAR_SHADER_DIR");
        var path = Path.Combine(programs, name + ".fssm");

        if (!File.Exists(path))
        {
            throw new ArgumentException($"no program {path}");
        }

        var hlsl = Translation(path, arguments.Flag(GroupWaveFlag));

        if (hlsl is null)
        {
            return Failure;
        }

        var target = Path.Combine(folder, name + ShaderOverrides.HlslExtension);

        if (File.Exists(target) && !arguments.Flag(ForceFlag))
        {
            throw new ArgumentException($"{target} exists (--force replaces it)");
        }

        Directory.CreateDirectory(folder);
        File.WriteAllText(target, ShaderOverrides.StartFrom(name, hlsl));
        Console.WriteLine($"{target}: the translation of {name}, to edit");

        return Success;
    }

    /// <summary>The program's translation (its wave32 group form with <paramref name="groupWave"/>); null, reported, when there is none.</summary>
    private static string? Translation(string path, bool groupWave)
    {
        var program = CorpusTranslator.TranslateFile(new ProgramLoader(new RdnaDecoder()), path, null, CorpusOptions.DefaultTranslation);

        if (program.Hlsl is null)
        {
            Console.Error.WriteLine($"not translated: {program.Status}: {program.Reason}");

            return null;
        }

        if (!groupWave)
        {
            return program.Hlsl;
        }

        if (program.GroupWave?.Hlsl is null)
        {
            Console.Error.WriteLine("the program has no wave32 group form");

            return null;
        }

        return program.GroupWave.Hlsl;
    }
}
