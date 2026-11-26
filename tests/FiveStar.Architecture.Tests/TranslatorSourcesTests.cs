// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;
using FiveStar.Architecture.Tests.Checking;
using Xunit;

namespace FiveStar.Architecture.Tests;

/// <summary>
/// The translation cache is keyed by the hash of FiveStar.Shaders' sources but Dxil/ and Cache/ (the TranslatorSources
/// target in its project file), which is sound only while no translation reaches those folders: no other source names
/// their namespaces, but the two that compile with the DXIL backend.
/// </summary>
public sealed partial class TranslatorSourcesTests
{
    private const string Project = "src/FiveStar.Shaders/";

    private static readonly string[] Excluded = ["Dxil/", "Cache/"];

    // They compile HLSL with the backend (LlvmPostPass runs over the backend's output, --dxil-llvm-opt); translations
    // never call them.
    private static readonly string[] BackendFiles = ["Compilation/DxilBackendCompiler.cs", "Compilation/InProcessBackend.cs", "Compilation/LlvmPostPass.cs"];

    [Fact]
    public void No_source_in_the_translator_hash_reaches_the_dxil_backend_or_the_cache_readers()
    {
        var root = CodeBaseLoader.FindRoot(AppContext.BaseDirectory);
        var folder = Path.Combine(root, Project);
        var reaching = new List<string>();

        foreach (var path in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, path).Replace('\\', '/');

            if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal)
                || Excluded.Any(excluded => relative.StartsWith(excluded, StringComparison.Ordinal)) || BackendFiles.Contains(relative))
            {
                continue;
            }

            reaching.AddRange(ReachingPattern().Matches(File.ReadAllText(path)).Select(match => $"{relative}: {match.Value}"));
        }

        Assert.True(reaching.Count == 0, "move them out of the hash's exclusions (FiveStar.Shaders.csproj):\n" + string.Join('\n', reaching));
    }

    // The namespaces, or a name qualified from inside FiveStar.Shaders (Dxil.Ir.X, Cache.ProgramIndex).
    [GeneratedRegex(@"FiveStar\.Shaders\.(?:Dxil|Cache)\b|(?<![\w.])(?:Dxil|Cache)\.[A-Z]\w*")]
    private static partial Regex ReachingPattern();
}
