// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using FiveStar.Loader.Shims.D3D12X.Pipelines;
using FiveStar.Shaders.Compilation;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// The compile host: the loader in compile-host mode compiles a program with the DXIL backend, and a host that is
/// killed is started again at the next request.
/// </summary>
public sealed class CompileHostTests
{
    private const string Program = """
        [numthreads(64, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID) {
          RWByteAddressBuffer output = ResourceDescriptorHeap[1];
          output.Store(id.x * 4u, id.x * 3u + 7u);
        }
        """;

    private static readonly TimeSpan EndLimit = TimeSpan.FromSeconds(30);

    [Fact]
    public void A_killed_host_is_started_again_at_the_next_request()
    {
        var work = Directory.CreateTempSubdirectory("fivestar-compile-host-");

        try
        {
            var host = new CompileHost(Path.Combine(AppContext.BaseDirectory, "fivestar-loader.exe"), null);
            var first = new CompileRequest(Path.Combine(work.FullName, "first.hlsl"), Program, "cs", DisableOptimizations: false);
            var second = first with { HlslPath = Path.Combine(work.FullName, "second.hlsl") };

            Assert.Equal(BackendVerdict.Compiled, host.Run(first).Verdict);
            Assert.True(File.Exists(first.DxilPath));

            using (var process = Process.GetProcessById(host.ProcessId!.Value))
            {
                process.Kill();
                process.WaitForExit();
            }

            Assert.True(SpinWait.SpinUntil(() => !host.Running, EndLimit));
            Assert.Equal(BackendVerdict.Compiled, host.Run(second).Verdict);
            Assert.True(File.Exists(second.DxilPath));
            Assert.StartsWith("compile host: 2 started, 1 ended", host.Summary, StringComparison.Ordinal);
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }
}
