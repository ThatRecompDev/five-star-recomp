// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Compilation;
using FiveStar.Shaders.Containers;
using FiveStar.Shaders.Translation;

namespace FiveStar.Cli.Shader;

/// <summary>
/// <c>shader dxil --compare</c>: compiles the same HLSL with dxc and checks that the backend's signatures are dxc's
/// (ISG1, OSG1 and PSG1 byte for byte, the PSV0 elements field by field), so a stage compiled by either links with the
/// other. Compute programs have no signatures to compare.
/// </summary>
internal sealed class SignatureCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);
    private static readonly string[] SignatureParts = ["ISG1", "OSG1", "PSG1"];

    private readonly DxcCompiler _dxc;
    private readonly string _folder;

    public SignatureCheck(DxcCompiler dxc, string folder)
    {
        _dxc = dxc;
        _folder = folder;
        Directory.CreateDirectory(folder);

        foreach (var name in RuntimeIncludes.Names)
        {
            File.WriteAllText(Path.Combine(folder, name), RuntimeIncludes.Read(name));
        }
    }

    /// <summary>The first difference from dxc's container, or null when the signatures agree.</summary>
    public string? Compare(string file, string hlsl, string profile, byte[] backend)
    {
        if (profile == "cs")
        {
            return null;
        }

        var source = Path.Combine(_folder, Path.GetFileName(file));
        File.WriteAllText(source, hlsl);
        var request = new CompileRequest(source, hlsl, profile, DisableOptimizations: false);
        var outcome = _dxc.Compile(request, Timeout);

        if (!outcome.Succeeded)
        {
            return "dxc: " + DxcCompiler.FirstError(outcome.Error ?? string.Empty);
        }

        var reference = DxbcContainer.Parse(File.ReadAllBytes(request.DxilPath));
        var mine = DxbcContainer.Parse(backend);

        foreach (var part in SignatureParts)
        {
            var expected = reference.Find(part)?.Data.ToArray() ?? [];
            var actual = mine.Find(part)?.Data.ToArray() ?? [];

            if (!expected.AsSpan().SequenceEqual(actual))
            {
                return $"{part} differs: dxc {Convert.ToHexString(expected)} backend {Convert.ToHexString(actual)}";
            }
        }

        var expectedElements = PsvSignatureParser.Parse(reference.Find(PsvSignatureParser.ChunkCode)!.Data.Span);
        var actualElements = PsvSignatureParser.Parse(mine.Find(PsvSignatureParser.ChunkCode)!.Data.Span);
        var pairs = expectedElements.Inputs.Concat(expectedElements.Outputs).Zip(actualElements.Inputs.Concat(actualElements.Outputs));
        var mismatch = pairs.FirstOrDefault(pair => pair.First != pair.Second);

        if (mismatch != default)
        {
            return $"PSV0 element differs: dxc {mismatch.First} backend {mismatch.Second}";
        }

        return null;
    }
}
