// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Tests.Differential;
using FiveStar.Shaders.Tests.Toolchain;
using FiveStar.Shaders.Translation;
using Xunit;
using Xunit.Abstractions;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// The uniform wave operations (Waves.UniformWaveOperations) of real wave64 compute programs of the user's dump: the
/// first programs of the sample (FIVESTAR_DIFF_PROGRAMS, default 3) whose superstep form the rewrite changes run in that
/// form with and without it on WARP with the same synthetic inputs, and every output word must be equal.
/// </summary>
public sealed class UniformSuperstepDifferentialTests : IClassFixture<WarpFixture>
{
    private const int DefaultPrograms = 3;

    // Programs looked at per one wanted: most compute programs are not wave64 or have no uniform wave operation.
    private const int Candidates = 40;

    private readonly WarpFixture _fixture;
    private readonly ITestOutputHelper _output;

    public UniformSuperstepDifferentialTests(WarpFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [RealProgramTheory]
    [InlineData("cs")]
    public void The_superstep_form_gives_the_same_outputs_with_and_without_uniform_wave_operations(string stage)
    {
        var count = DefaultPrograms;

        if (int.TryParse(Environment.GetEnvironmentVariable(ControlFlowDifferentialTests.ProgramsVariable), out var programs))
        {
            count = programs;
        }

        var runner = new DifferentialRunner(_fixture, Path.Combine(_fixture.Folder, "uniform-supersteps"), backend: false);
        var outcomes = new List<DifferentialOutcome>();

        foreach (var path in ProgramSample.Of(RealProgramTheoryAttribute.Folder!, ShaderStages.FromProfileName(stage)!.Value, count * Candidates))
        {
            var outcome = runner.RunUniformSupersteps(path);
            _output.WriteLine($"{outcome.Name}: {outcome.Verdict} {outcome.Detail}");

            if (outcome.Verdict is DifferentialVerdict.Skipped or DifferentialVerdict.SameText)
            {
                continue;
            }

            outcomes.Add(outcome);

            if (outcomes.Count == count)
            {
                break;
            }
        }

        var mismatches = outcomes.Where(outcome => outcome.Verdict == DifferentialVerdict.Mismatch).ToList();

        Assert.NotEmpty(outcomes);
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Select(outcome => $"{outcome.Name}: {outcome.Detail}")));
    }
}
