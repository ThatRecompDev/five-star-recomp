// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Graphics.Replay;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Shims.D3D12X.Pipelines;
using FiveStar.Shaders.Compilation;
using FiveStar.Shaders.Disassembly;
using FiveStar.Shaders.Translation;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// A capture's program files under <c>program/</c>: the DXIL the pipeline was built from (<see cref="DispatchCaptures.NoteProgram"/>),
/// the console program (FSSM, --shader-dir), the HLSL (the ahead-of-time translation's file when it is there, else the
/// form made on demand: translated now with this process's options, after the defines its modes put before it) and
/// the runtime headers it includes.
/// </summary>
internal static class CaptureProgram
{
    private const string Folder = "program/";
    private const string Includes = "program/includes";
    private const string NameSeparator = " + ";
    private const string ComputeProfile = "cs";

    public static CapturedProgram Write(CaptureFolder folder, string pipeline)
    {
        string name = pipeline.Split(NameSeparator)[0];
        string? dxil = null;
        var code = DispatchCaptures.Dxil(name);

        if (code is not null)
        {
            dxil = folder.WriteFile(Folder + "program.dxil", code);
        }

        string? fssm = null;
        var console = TableReaches.ProgramFolder is null ? null : Path.Combine(TableReaches.ProgramFolder, TableReaches.Source(name) + ".fssm");

        if (console is not null && File.Exists(console))
        {
            fssm = folder.WriteFile(Folder + "program.fssm", File.ReadAllBytes(console));
        }

        foreach (var include in RuntimeIncludes.Names)
        {
            folder.WriteText($"{Includes}/{include}", RuntimeIncludes.Read(include));
        }

        var (hlsl, header) = Hlsl(name, console);
        string? kept = null;

        if (hlsl is not null)
        {
            kept = folder.WriteText(Folder + "program.hlsl", hlsl);
        }

        return new CapturedProgram(name, ComputeProfile, dxil, kept, fssm, header) { Translation = OnDemandStart.Translation.ToString(), Includes = Includes };
    }

    /// <summary>The HLSL and the defines before it: the translation folder's file (no defines), else translated now.</summary>
    private static (string? Hlsl, string? Header) Hlsl(string name, string? console)
    {
        var translated = DispatchCaptures.TranslatedFolder is null ? null : Path.Combine(DispatchCaptures.TranslatedFolder, name + ".hlsl");

        if (translated is not null && File.Exists(translated))
        {
            return (File.ReadAllText(translated), null);
        }

        if (console is null || !File.Exists(console))
        {
            return (null, null);
        }

        var program = CorpusTranslator.TranslateFile(new ProgramLoader(new RdnaDecoder()), console, null, OnDemandStart.Translation);

        if (program.Hlsl is null)
        {
            return (null, null);
        }

        string modes = ProgramProducer.ModeHeader();
        string header = modes + WindowGuard.ProgramHeader(modes, name);

        return (header + program.Hlsl, header);
    }
}
