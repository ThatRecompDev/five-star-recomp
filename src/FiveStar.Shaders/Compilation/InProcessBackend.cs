// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using FiveStar.Shaders.Dxil;
using FiveStar.Shaders.Dxil.Validation;
using FiveStar.Shaders.Translation;

namespace FiveStar.Shaders.Compilation;

/// <summary>
/// The DXIL backend in this process: compiles a request, checks the container with dxil.dll's validator when one is
/// given (a validator per concurrent compile) and writes it through a temporary file. The runtime headers come from
/// the source's folder when it has them, else from this assembly. Runs on any threads.
/// </summary>
public sealed class InProcessBackend : IBackendRunner
{
    private const string TemporaryExtension = ".tmp";

    private readonly string? _validatorPath;
    private readonly LlvmPostPass? _postPass;
    private readonly ConcurrentBag<DxilValidator> _validators = [];

    /// <param name="validatorPath">dxil.dll, whose validator checks every container; null checks none.</param>
    /// <param name="postPass">LLVM passes over each container (<c>--dxil-llvm-opt</c>); null runs none.</param>
    public InProcessBackend(string? validatorPath, LlvmPostPass? postPass)
    {
        _validatorPath = validatorPath;
        _postPass = postPass;
    }

    public bool Validates => _validatorPath is not null;

    public string PostPass
    {
        get
        {
            if (_postPass is null)
            {
                return string.Empty;
            }

            return LlvmPostPass.Identity;
        }
    }

    public BackendResult Run(CompileRequest request)
    {
        if (!DxilBackend.Supports(request.Profile))
        {
            return new BackendResult(BackendVerdict.NotLowered, $"profile {request.Profile} is not supported");
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(request.HlslPath)) ?? ".";
        byte[] container;

        try
        {
            container = DxilBackend.Compile(request.Hlsl, request.Profile, name => Include(folder, name));
        }
        catch (DxilBackendException exception)
        {
            return new BackendResult(BackendVerdict.NotLowered, exception.Message);
        }

        var rejected = Validate(container);

        if (rejected is null && _postPass is not null)
        {
            container = PostPassed(container);
        }

        if (rejected is not null)
        {
            return new BackendResult(BackendVerdict.Rejected, "validation: " + rejected);
        }

        var temporary = request.DxilPath + TemporaryExtension;
        File.WriteAllBytes(temporary, container);
        File.Move(temporary, request.DxilPath, overwrite: true);

        return BackendResult.Compiled;
    }

    /// <summary>The container after the LLVM post-pass, or as it was when the passes fail or the validator rejects their output.</summary>
    private byte[] PostPassed(byte[] container)
    {
        byte[] optimized;

        try
        {
            optimized = _postPass!.Optimize(container);
        }
        catch (InvalidOperationException)
        {
            return container;
        }

        if (Validate(optimized) is not null)
        {
            return container;
        }

        return optimized;
    }

    /// <summary>The validator's messages when it rejects the container, else null.</summary>
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

    private static string? Include(string folder, string name)
    {
        var path = Path.Combine(folder, name);

        if (File.Exists(path))
        {
            return File.ReadAllText(path);
        }

        return RuntimeIncludes.Read(name);
    }
}
