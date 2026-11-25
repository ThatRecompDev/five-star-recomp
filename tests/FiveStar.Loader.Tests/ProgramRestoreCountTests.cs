// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Graphics;
using FiveStar.Graphics.Programs;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Shims.D3D12X.Pipelines;
using FiveStar.Shaders.Cache;
using FiveStar.Shaders.Compilation;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// The on-demand summary tells programs restored from the content store from programs compiled: after a translator-only
/// change most come back from the store, and a boot that counted them all as compiled looked cold when it was not.
/// </summary>
public sealed class ProgramRestoreCountTests : IDisposable
{
    private const string Hlsl = "[numthreads(1, 1, 1)] void main() { }";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("fivestar-restore-");

    [Fact]
    public void A_program_whose_source_the_store_has_counts_as_restored_not_compiled()
    {
        var compiler = new CountingCompiler();
        var store = new ProgramContentStore(Path.Combine(Folder.FullName, ProgramContentStore.FolderName), "test");
        var warnings = new OnceLog(new LoaderLog(new StringWriter(), null, false));
        var picker = new ProgramVariantPicker(new ShaderManifest([]), new AdapterInfo(0, string.Empty, false, null, 32, 64, null), warnings);
        var producer = new ProgramProducer(Folder.FullName, Folder.FullName, picker, compiler, SlowCompiles.Read([], compiler.Options.CompileTimeout)) { ContentStore = store };

        // The same source under two program names: the second is copied from the store.
        Assert.Null(producer.Compile(Form("first")));
        Assert.Null(producer.Compile(Form("second")));

        Assert.Equal((1, 1), (compiler.Compiles, producer.Restored));
        Assert.True(File.Exists(Path.Combine(Folder.FullName, "second.dxil")));
        Assert.Equal("2 translated, of which 1 restored from the content store, 1 compiled", OnDemandPrograms.Describe(2, producer.Restored));
    }

    [Fact]
    public void The_compile_host_says_when_its_answer_came_from_the_store()
    {
        using var stream = new MemoryStream();
        CompileHostProtocol.WriteAnswer(new BinaryWriter(stream), 7, HostAnswer.Restored);
        stream.Position = 0;

        var (id, answer) = CompileHostProtocol.ReadAnswer(new BinaryReader(stream));

        Assert.Equal((7, true), (id, answer.WasRestored));
        Assert.False(HostAnswer.Compiled.WasRestored);
    }

    /// <summary>A program folder as the loader lays it out: &lt;cache&gt;/&lt;identity&gt;/&lt;adapter&gt;.</summary>
    private DirectoryInfo Folder => Directory.CreateDirectory(Path.Combine(_root.FullName, "identity", "32"));

    public void Dispose()
    {
        _root.Delete(recursive: true);
    }

    private ProgramForm Form(string name)
    {
        return new ProgramForm(name, new CompileRequest(Path.Combine(Folder.FullName, name + ".src.hlsl"), Hlsl, "cs", false), null);
    }

    /// <summary>Writes a DXIL file for every compile and counts them.</summary>
    private sealed class CountingCompiler : IShaderCompiler
    {
        public int Compiles { get; private set; }

        public DxcOptions Options { get; } = DxcOptions.Defaults(string.Empty, retry: false);

        public string Identity => "counting";

        public CompileOutcome Compile(CompileRequest request, TimeSpan timeout)
        {
            Compiles++;
            File.WriteAllBytes(request.DxilPath, [1, 2, 3, 4]);

            return CompileOutcome.Success;
        }
    }
}
