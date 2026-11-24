// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using FiveStar.Shaders.Dxil;
using FiveStar.Shaders.Dxil.Validation;
using FiveStar.Shaders.Translation;

namespace FiveStar.Shaders.Compilation;

/// <summary>
/// Compiles translated HLSL with the DXIL backend (no dxc; docs/dxil-backend.md). A program the backend does not
/// handle (another stage, or HLSL it cannot lower), or whose container dxil.dll's validator rejects when one is given,
/// goes to <c>fallback</c> when there is one, else fails with the backend's reason. The runtime headers come from the
/// source's folder when it has them, else from this assembly. Compiles may run on any threads.
/// </summary>
public sealed class DxilBackendCompiler : IShaderCompiler
{
    private const string Prefix = "dxil backend: ";
    private const string TemporaryExtension = ".tmp";
    private const string ValidatorFile = "dxil.dll";

    private readonly IShaderCompiler? _fallback;
    private readonly string? _validatorPath;
    private readonly ConcurrentBag<DxilValidator> _validators = [];
    private readonly Lazy<string> _identity;
    private int _compiled;
    private int _unlowered;
    private int _invalid;

    /// <param name="options">Limits, passed on to the fallback.</param>
    /// <param name="fallback">Compiles what the backend cannot; null fails instead.</param>
    /// <param name="validatorPath">dxil.dll, whose validator checks every container; null checks none.</param>
    public DxilBackendCompiler(DxcOptions options, IShaderCompiler? fallback, string? validatorPath)
    {
        Options = options;
        _fallback = fallback;
        _validatorPath = validatorPath;
        _identity = new Lazy<string>(ComputeIdentity);
    }

    public DxcOptions Options { get; }

    public string Identity => _identity.Value;

    /// <summary>Programs the backend compiled, those it could not lower and those the validator rejected (both went to the fallback).</summary>
    public (int Compiled, int Unlowered, int Invalid) Counts => (Volatile.Read(ref _compiled), Volatile.Read(ref _unlowered), Volatile.Read(ref _invalid));

    /// <summary>The counts as a summary: "DXIL backend N, dxc M (U not lowered, I rejected by validation)".</summary>
    public string Summary
    {
        get
        {
            var (compiled, unlowered, invalid) = Counts;

            return $"DXIL backend {compiled}, dxc {unlowered + invalid} ({unlowered} not lowered, {invalid} rejected by validation)";
        }
    }

    public CompileOutcome Compile(CompileRequest request, TimeSpan timeout)
    {
        if (!DxilBackend.Supports(request.Profile))
        {
            return Fallback(request, timeout, ref _unlowered, $"profile {request.Profile} is not supported");
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(request.HlslPath)) ?? ".";
        byte[] container;

        try
        {
            container = DxilBackend.Compile(request.Hlsl, request.Profile, name => Include(folder, name));
        }
        catch (DxilBackendException exception)
        {
            return Fallback(request, timeout, ref _unlowered, exception.Message);
        }

        var rejected = Validate(container);

        if (rejected is not null)
        {
            return Fallback(request, timeout, ref _invalid, "validation: " + rejected);
        }

        var temporary = request.DxilPath + TemporaryExtension;
        File.WriteAllBytes(temporary, container);
        File.Move(temporary, request.DxilPath, overwrite: true);
        Interlocked.Increment(ref _compiled);

        return CompileOutcome.Success;
    }

    /// <summary>The dxil.dll beside a dxc.exe, a dxcompiler.dll or in their folder; null when there is none.</summary>
    public static string? ValidatorBeside(string dxc)
    {
        var path = Path.Combine(Path.GetDirectoryName(DxcLibraryCompiler.LibraryPath(dxc)) ?? ".", ValidatorFile);

        if (!File.Exists(path))
        {
            return null;
        }

        return path;
    }

    /// <summary>The validator's messages when it rejects the container, else null; one validator per concurrent compile.</summary>
    private string? Validate(byte[] container)
    {
        if (_validatorPath is null)
        {
            return null;
        }

        if (!_validators.TryTake(out var validator))
        {
            validator = DxilValidator.Load(_validatorPath);
        }

        try
        {
            return validator.Validate(container);
        }
        finally
        {
            _validators.Add(validator);
        }
    }

    private CompileOutcome Fallback(CompileRequest request, TimeSpan timeout, ref int count, string reason)
    {
        Interlocked.Increment(ref count);

        if (_fallback is null)
        {
            return CompileOutcome.Failure(Prefix + reason);
        }

        return _fallback.Compile(request, timeout);
    }

    private static string? Include(string folder, string name)
    {
        var path = Path.Combine(folder, name);

        if (File.Exists(path))
        {
            return File.ReadAllText(path);
        }

        return RuntimeIncludes.Read(name);
    }

    /// <summary>This build of the backend (its module version id, a hash of the deterministic build), whether it validates, and the fallback's identity.</summary>
    private string ComputeIdentity()
    {
        var assembly = typeof(DxilBackend).Assembly.ManifestModule.ModuleVersionId.ToString();
        var validated = _validatorPath is null ? string.Empty : "validated";

        return ContentHash.OfText(["dxil-backend", assembly, validated, _fallback?.Identity ?? string.Empty]);
    }
}
