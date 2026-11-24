// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Compilation;
using FiveStar.Shaders.Tests.Toolchain;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// The backend as the loader's on-demand compiler: a program it lowers is validated by dxil.dll and written, and one it
/// cannot lower goes to the fallback; both are counted.
/// </summary>
public sealed class DxilBackendCompilerTests
{
    private const string Lowered = """
        #define VW_WAVE 64
        #define VW_EMULATE 1
        #include "rdna_prelude.hlsli"
        [numthreads(64, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID) {
          uint v0 = WaveActiveSum(id.x);
          if (v0 > 3u) { v0 = v0 * 2u; }
        }
        """;

    // Function-like macros are outside the dialect the backend lowers.
    private const string NotLowered = """
        #define TWICE(x) ((x) * 2u)
        [numthreads(64, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID) {
          uint v0 = TWICE(id.x);
        }
        """;

    [WarpFact]
    public void A_program_the_backend_cannot_lower_goes_to_the_fallback_and_the_rest_is_validated()
    {
        var work = Directory.CreateTempSubdirectory("fivestar-backend-");

        try
        {
            var dxc = ShaderToolchain.Find().Dxc;
            var fallback = new RecordingCompiler(DxcOptions.Defaults(dxc, retry: false));
            var compiler = new DxilBackendCompiler(fallback.Options, fallback, DxilBackendCompiler.ValidatorBeside(dxc));
            var lowered = new CompileRequest(Path.Combine(work.FullName, "lowered.hlsl"), Lowered, "cs", DisableOptimizations: false);
            var notLowered = new CompileRequest(Path.Combine(work.FullName, "other.hlsl"), NotLowered, "cs", DisableOptimizations: false);

            Assert.True(compiler.Compile(lowered, TimeSpan.Zero).Succeeded);
            Assert.True(File.Exists(lowered.DxilPath));
            Assert.Empty(fallback.Requests);

            Assert.True(compiler.Compile(notLowered, TimeSpan.Zero).Succeeded);
            Assert.Equal([notLowered], fallback.Requests);
            Assert.Equal((1, 1, 0), compiler.Counts);
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    private sealed class RecordingCompiler(DxcOptions options) : IShaderCompiler
    {
        public List<CompileRequest> Requests { get; } = [];

        public DxcOptions Options => options;

        public string Identity => nameof(RecordingCompiler);

        public CompileOutcome Compile(CompileRequest request, TimeSpan timeout)
        {
            Requests.Add(request);

            return CompileOutcome.Success;
        }
    }
}
