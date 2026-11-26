// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Cache;
using FiveStar.Shaders.Compilation;
using FiveStar.Shaders.Translation;
using Xunit;

namespace FiveStar.Shaders.Tests.Compilation;

/// <summary>The translation cache's identity: the translator's source hash from the build, and the options.</summary>
public sealed class TranslatorSourcesTests
{
    [Fact]
    public void The_build_sets_the_translator_hash_apart_from_the_backend_hash()
    {
        Assert.False(Guid.TryParse(TranslationCache.TranslatorSources, out _), "the TranslatorSources target did not run: the module id stands in");
        Assert.NotEqual(ProgramContentStore.CompilerSources, TranslationCache.TranslatorSources);
    }

    [Fact]
    public void The_identity_follows_the_options_and_nothing_else()
    {
        var setup = new TranslationOptions(StructuredControlFlow: true, PinWaveSize: true, EmulateWaveSize: true);

        Assert.Equal(TranslationCache.Identity(null, setup), TranslationCache.Identity(null, setup with { }));
        Assert.NotEqual(TranslationCache.Identity(null, setup), TranslationCache.Identity(null, setup with { FastWaves = true }));
    }
}
