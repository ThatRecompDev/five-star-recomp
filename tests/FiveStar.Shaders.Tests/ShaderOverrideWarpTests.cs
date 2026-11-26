// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Compilation;
using FiveStar.Shaders.Tests.Cases;
using FiveStar.Shaders.Tests.Toolchain;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// A hand-written replacement (--shader-overrides) started from a compute case's translation, as override-init writes
/// it, and edited: it runs on WARP in place of the translation with the edit's result, and its cache stem holds its
/// content hash, so the translation and each edit are kept apart.
/// </summary>
public sealed class ShaderOverrideWarpTests : IClassFixture<WarpFixture>
{
    private const string CaseName = "cs_user_sgprs";
    private const string Translated = "v22 = select(active, (uint)(s11), v22);";
    private const string Edited = "v22 = select(active, user_sgpr(10), v22);";

    private readonly WarpFixture _fixture;

    public ShaderOverrideWarpTests(WarpFixture fixture)
    {
        _fixture = fixture;
    }

    [WarpFact]
    public void An_edited_replacement_runs_in_place_of_the_translation_under_its_own_stem()
    {
        var testCase = ComputeCases.Find(CaseName);
        var runner = new ComputeCaseRunner(_fixture);
        var folder = Directory.CreateTempSubdirectory("fivestar-overrides-");

        try
        {
            Assert.Empty(runner.Run(testCase));
            var path = Path.Combine(folder.FullName, CaseName + ShaderOverrides.HlslExtension);
            ShaderOverrides? overrides = null;

            // v22 took s11 (0x44444444); the edit gives it user SGPR 10 (0x33333333) through the prelude's user_sgpr.
            var edited = testCase with { Expect = thread => [0x11111111, 0x22222222, 0x33333333, thread.Id / 64, 0x33333333] };
            var mismatches = runner.RunReplaced(edited, hlsl =>
            {
                Assert.Contains(Translated, hlsl, StringComparison.Ordinal);
                File.WriteAllText(path, ShaderOverrides.StartFrom(CaseName, hlsl).Replace(Translated, Edited, StringComparison.Ordinal));
                overrides = ShaderOverrides.Open(folder.FullName);

                return overrides.Hlsl(CaseName)!;
            });

            Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Take(8)));
            var stem = overrides!.Stem(CaseName);
            Assert.StartsWith(CaseName + ShaderOverrides.StemInfix, stem, StringComparison.Ordinal);
            Assert.Equal("cs_other", overrides.Stem("cs_other"));

            // Another edit, another stem: the cache never serves the previous one's DXIL.
            File.AppendAllText(path, "\n// another edit\n");
            Assert.NotEqual(stem, ShaderOverrides.Open(folder.FullName).Stem(CaseName));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
