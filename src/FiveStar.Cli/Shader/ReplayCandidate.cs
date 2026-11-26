// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using FiveStar.Graphics.Replay;
using FiveStar.Shaders.Compilation;
using FiveStar.Shaders.Disassembly;
using FiveStar.Shaders.Translation;

namespace FiveStar.Cli.Shader;

/// <summary>
/// The program a replay runs: the captured DXIL, a hand-edited HLSL file, or the captured console program translated
/// by this build (the default translation options of <c>fivestar shader translate</c>, the loader's defines before it),
/// compiled in <c>replay-work</c> of the capture folder; null compile time for the captured DXIL.
/// </summary>
internal sealed record ReplayCandidate(string Source, byte[] Dxil, double? CompileMilliseconds)
{
    private const string WorkFolder = "replay-work";
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The captured DXIL, as the pipeline was built from it.</summary>
    public static ReplayCandidate Captured(CaptureFolder folder, DispatchCapture capture)
    {
        if (capture.Program.Dxil is null)
        {
            throw new ArgumentException("the capture holds no DXIL; replay it with --hlsl or --translator current");
        }

        return new ReplayCandidate("captured DXIL", folder.ReadFile(capture.Program.Dxil), null);
    }

    /// <summary>An HLSL file compiled as it is, with the capture's runtime headers beside it when it kept them.</summary>
    public static ReplayCandidate FromHlsl(CaptureFolder folder, DispatchCapture capture, string path, IShaderCompiler compiler)
    {
        var work = Work(folder, capture, useCaptured: true);

        return Compile(compiler, work, capture.Program, File.ReadAllText(path), $"HLSL {path}");
    }

    /// <summary>
    /// The captured FSSM translated by this build, with the defines the loader put before the program; with
    /// <paramref name="groupWave"/> the wave32 variant of a wave64-pinned program, as the loader runs it on such GPUs.
    /// </summary>
    public static ReplayCandidate FromTranslator(CaptureFolder folder, DispatchCapture capture, IShaderCompiler compiler, bool groupWave = false)
    {
        if (capture.Program.Fssm is null)
        {
            throw new ArgumentException("the capture holds no console program (FSSM) to translate");
        }

        var loader = new ProgramLoader(new RdnaDecoder());
        var program = CorpusTranslator.TranslateFile(loader, folder.PathOf(capture.Program.Fssm), null, CorpusOptions.DefaultTranslation);

        if (program.Hlsl is null)
        {
            throw new InvalidOperationException($"the translator refused {capture.Program.Name}: {program.Status} {program.Reason}");
        }

        var body = program.Hlsl;

        if (groupWave)
        {
            body = program.GroupWave?.Hlsl ?? throw new InvalidOperationException($"{capture.Program.Name} has no wave32 variant: {program.GroupWave?.Reason ?? "not wave64-pinned"}");
        }

        var work = Work(folder, capture, useCaptured: false);
        var hlsl = (capture.Program.Header ?? string.Empty) + body;

        return Compile(compiler, work, capture.Program, hlsl, groupWave ? "current translator, wave32 variant" : "current translator");
    }

    private static ReplayCandidate Compile(IShaderCompiler compiler, string work, CapturedProgram program, string hlsl, string source)
    {
        var path = Path.Combine(work, "candidate.hlsl");
        File.WriteAllText(path, hlsl);
        var clock = Stopwatch.StartNew();
        var outcome = compiler.Compile(new CompileRequest(path, hlsl, program.Profile, DisableOptimizations: false), CompileTimeout);
        clock.Stop();

        if (!outcome.Succeeded)
        {
            throw new InvalidOperationException($"{source} did not compile: {DxcCompiler.FirstError(outcome.Error)}");
        }

        return new ReplayCandidate(source, File.ReadAllBytes(Path.ChangeExtension(path, ".dxil")), clock.Elapsed.TotalMilliseconds);
    }

    /// <summary>The work folder with the runtime headers: the capture's own when <paramref name="useCaptured"/> and kept, else this build's.</summary>
    private static string Work(CaptureFolder folder, DispatchCapture capture, bool useCaptured)
    {
        var work = folder.PathOf(WorkFolder);
        RuntimeIncludes.WriteTo(work);

        if (!useCaptured || capture.Program.Includes is null)
        {
            return work;
        }

        foreach (var name in RuntimeIncludes.Names)
        {
            var kept = Path.Combine(folder.PathOf(capture.Program.Includes), name);

            if (File.Exists(kept))
            {
                File.Copy(kept, Path.Combine(work, name), overwrite: true);
            }
        }

        return work;
    }
}
