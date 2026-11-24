// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using FiveStar.Graphics.Programs;
using FiveStar.Shaders.Analysis;
using FiveStar.Shaders.Compilation;
using FiveStar.Shaders.Containers;
using FiveStar.Shaders.Disassembly;
using FiveStar.Shaders.Translation;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// Makes the DXIL of one extracted program for this adapter, as <c>fivestar shader translate</c> would: translates the
/// FSSM program with the default translator choices, picks the form the adapter runs (<see cref="ProgramVariantPicker"/>:
/// native, wave32 variant or mesh superstep form) and compiles only that one into <c>&lt;folder&gt;/&lt;name&gt;.dxil</c>.
/// A mesh fallback is not compiled: pipelines cannot run it. Sources over the large-source length, or whose optimized
/// compile was slow before (<see cref="SlowCompiles"/>), compile with -Od. A failure that would repeat (the translator
/// refuses the program, no form loads, the compiler reports a source error) is kept as <c>&lt;name&gt;.fail</c>.
/// </summary>
internal sealed class ProgramProducer
{
    private const string ProgramExtension = ".fssm";
    private const string DxilExtension = ".dxil";
    private const string HlslExtension = ".hlsl";
    private const string FailExtension = ".fail";
    private const string VariantDxilSuffix = ".w32.dxil";
    private const string NoForm = "no form: none loads on this adapter";
    private const string NoProgram = "missing: no extracted program file";

    // A compile failure with a source or validation diagnostic repeats; a crash or exhausted memory may not.
    private const string SourceError = ": error: ";
    private const string ValidationError = "error: validation errors";

    private readonly string _programs;
    private readonly string _folder;
    private readonly ProgramVariantPicker _picker;
    private readonly IShaderCompiler _compiler;
    private readonly SlowCompiles _slow;
    private readonly ProgramLoader _loader = new(new RdnaDecoder());

    /// <param name="programs">The extracted FSSM programs (--shader-dir), their containers in the sibling dxbc folder.</param>
    /// <param name="folder">Where the DXIL goes: the folder of this translator, compiler and adapter.</param>
    /// <param name="picker">Picks the form for the adapter.</param>
    /// <param name="compiler">Compiles the picked form.</param>
    /// <param name="slow">Sources compiled with -Od from the start.</param>
    public ProgramProducer(string programs, string folder, ProgramVariantPicker picker, IShaderCompiler compiler, SlowCompiles slow)
    {
        _programs = programs;
        _folder = folder;
        _picker = picker;
        _compiler = compiler;
        _slow = slow;
    }

    /// <summary>The folder the DXIL goes to.</summary>
    public string Folder => _folder;

    /// <summary>Whether the program's DXIL is in the folder.</summary>
    public bool Exists(string name)
    {
        return File.Exists(DxilPath(name));
    }

    /// <summary>The failure kept for the program, or null.</summary>
    public string? KeptFailure(string name)
    {
        var path = Path.Combine(_folder, name + FailExtension);

        if (!File.Exists(path))
        {
            return null;
        }

        return File.ReadAllText(path);
    }

    /// <summary>Translates the program and picks its form; a refusal is kept.</summary>
    public ProgramForm Prepare(string name)
    {
        var path = Path.Combine(_programs, name + ProgramExtension);

        if (!File.Exists(path))
        {
            return new ProgramForm(name, null, NoProgram);
        }

        var program = CorpusTranslator.TranslateFile(_loader, path, null, CorpusOptions.DefaultTranslation);

        if (program.Hlsl is null || program.Stage is null)
        {
            return Refused(name, $"{program.Status ?? ProgramStatus.Crash}: {program.Reason}");
        }

        var profile = ShaderStages.ProfileName(program.Stage.Value);
        var facts = WaveFacts.Read(program.Hlsl);
        string? variant = null;

        if (program.GroupWave?.Hlsl is not null)
        {
            variant = name + VariantDxilSuffix;
        }

        var forms = new ProgramVariants(name, profile, name + DxilExtension, facts.ProgramWave, facts.Floor, facts.WavePin, variant, null);
        var file = _picker.Pick(forms);

        if (file is null)
        {
            return Refused(name, NoForm);
        }

        var (hlsl, source) = (program.Hlsl, name + HlslExtension);

        if (file == variant)
        {
            (hlsl, source) = (program.GroupWave!.Hlsl!, name + CorpusTranslator.GroupWaveSuffix);
        }

        if (DebugLayer.PoolStoreTrace.Enabled)
        {
            hlsl = DebugLayer.PoolStoreTrace.Header(name) + hlsl;
        }

        var slow = hlsl.Length > _compiler.Options.LargeSourceLength || _slow.Contains(source);

        return new ProgramForm(name, new CompileRequest(Path.Combine(_folder, source), hlsl, profile, slow), null);
    }

    /// <summary>Compiles a prepared form into <c>&lt;name&gt;.dxil</c>; null when it did, else why not.</summary>
    public string? Compile(ProgramForm form)
    {
        var request = form.Request!;
        var outcome = _compiler.Compile(request, _compiler.Options.CompileTimeout);

        if (!outcome.Succeeded)
        {
            var failure = $"{ProgramStatus.CompileFailure}: {DxcCompiler.FirstError(outcome.Error)}";

            if (outcome.Error.Contains(SourceError, StringComparison.Ordinal) || outcome.Error.Contains(ValidationError, StringComparison.Ordinal))
            {
                Keep(form.Name, failure);
            }

            return failure;
        }

        var target = DxilPath(form.Name);

        if (!string.Equals(request.DxilPath, target, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(request.DxilPath, target, overwrite: true);
        }

        return null;
    }

    /// <summary>
    /// Compiles the program's form with its outputs laid out for a pixel shader's <paramref name="inputs"/>
    /// (<see cref="StageLinker.Link"/>) into <paramref name="dxilPath"/>; null when it did, else why not (kept beside it).
    /// </summary>
    public string? Link(string name, IReadOnlyList<PsvElement> inputs, string dxilPath)
    {
        return Remake(name, hlsl => StageLinker.Link(hlsl, inputs), dxilPath);
    }

    /// <summary>Compiles a pixel shader without the inputs it never reads (<see cref="StageLinker.TrimInputs"/>), as <see cref="Link"/>.</summary>
    public string? Trim(string name, string dxilPath)
    {
        return Remake(name, StageLinker.TrimInputs, dxilPath);
    }

    private string? Remake(string name, Func<string, string> rewrite, string dxilPath)
    {
        var failurePath = Path.ChangeExtension(dxilPath, FailExtension);

        if (File.Exists(failurePath))
        {
            return File.ReadAllText(failurePath);
        }

        var path = Path.Combine(_programs, name + ProgramExtension);

        if (!File.Exists(path))
        {
            return NoProgram;
        }

        var form = Prepare(name);
        string? failure;

        if (form.Request is null)
        {
            failure = form.Failure;
        }
        else
        {
            failure = CompileRewritten(form.Request, rewrite, dxilPath);
        }

        if (failure is not null)
        {
            File.WriteAllText(failurePath, failure, Encoding.UTF8);
        }

        return failure;
    }

    private string? CompileRewritten(CompileRequest request, Func<string, string> rewrite, string dxilPath)
    {
        string linked;

        try
        {
            linked = rewrite(request.Hlsl);
        }
        catch (UnsupportedProgramException exception)
        {
            return $"link: {exception.Message}";
        }

        var linkedRequest = request with { HlslPath = Path.ChangeExtension(dxilPath, HlslExtension), Hlsl = linked };
        var outcome = _compiler.Compile(linkedRequest, _compiler.Options.CompileTimeout);

        if (outcome.Succeeded)
        {
            return null;
        }

        // The source and the compiler's whole report stay beside the failure, for a look at why.
        File.WriteAllText(linkedRequest.HlslPath, linked, Encoding.UTF8);
        File.WriteAllText(Path.ChangeExtension(dxilPath, ".log"), outcome.Error, Encoding.UTF8);

        return $"link {ProgramStatus.CompileFailure}: {DxcCompiler.FirstError(outcome.Error)}";
    }

    private string DxilPath(string name)
    {
        return Path.Combine(_folder, name + DxilExtension);
    }

    private ProgramForm Refused(string name, string failure)
    {
        Keep(name, failure);

        return new ProgramForm(name, null, failure);
    }

    private void Keep(string name, string failure)
    {
        var path = Path.Combine(_folder, name + FailExtension);
        File.WriteAllText(path + ".tmp", failure, Encoding.UTF8);
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
