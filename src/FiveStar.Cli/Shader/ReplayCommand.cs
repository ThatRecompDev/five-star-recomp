// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using FiveStar.Graphics;
using FiveStar.Graphics.Replay;

namespace FiveStar.Cli.Shader;

/// <summary>
/// <c>fivestar shader replay &lt;capture&gt;</c>: runs a dispatch the loader captured (<c>--capture-dispatch</c>) on a
/// fresh device with the captured program or a candidate, compares every resource it writes with the captured output
/// and times it with GPU timestamps (docs/loader.md, Dispatch capture).
/// </summary>
internal static class ReplayCommand
{
    private const int Success = 0;
    private const int Different = 1;
    private const int DefaultRuns = 10;
    private const int DefaultShown = 8;
    private const string DebugLayerFlag = "debug-layer";
    private const string Warp = "warp";
    private const string Hardware = "hardware";
    private const string CurrentTranslator = "current";

    public static int Run(IReadOnlyList<string> args)
    {
        var arguments = new CommandArguments(args, new HashSet<string> { DebugLayerFlag });

        if (arguments.Positional.Count != 1)
        {
            throw new ArgumentException("replay <capture folder> [--adapter hardware|warp|<index>] [--hlsl <file> | --translator current] [--runs N] [--tolerance F] [--shown N] [--backend hlsl|dxil|dxil-only] [--debug-layer]");
        }

        var folder = new CaptureFolder(arguments.Positional[0]);
        var capture = folder.Read();
        var candidate = Candidate(arguments, folder, capture);

        using var device = Device(arguments.Option("adapter") ?? Hardware, arguments.Flag(DebugLayerFlag));
        var tolerance = Tolerance(arguments.Option("tolerance"));
        var report = Replay(device, folder, capture, candidate, arguments.IntegerOption("runs") ?? DefaultRuns, tolerance, arguments.IntegerOption("shown") ?? DefaultShown);
        Print(capture, candidate, device, report);

        if (report.Differences.All(difference => difference.Same))
        {
            return Success;
        }

        return Different;
    }

    /// <summary>Runs <paramref name="candidate"/> over the capture and compares each output with its reference.</summary>
    internal static ReplayReport Replay(GpuDevice device, CaptureFolder folder, DispatchCapture capture, ReplayCandidate candidate, int runs, float? tolerance, int shown)
    {
        var outcome = new DispatchReplay(device.Device).Run(folder, capture, candidate.Dxil, runs);
        var differences = new List<OutputDifference>();

        foreach (var resource in capture.Resources.Where(resource => resource.Output is not null))
        {
            var expected = folder.ReadFile(resource.Output!);
            differences.Add(OutputComparison.Compare(resource, expected, outcome.Outputs[resource.Id], tolerance, shown));
        }

        return new ReplayReport(outcome, differences, device.TakeErrors());
    }

    private static ReplayCandidate Candidate(CommandArguments arguments, CaptureFolder folder, DispatchCapture capture)
    {
        var hlsl = arguments.Option("hlsl");
        var translator = arguments.Option("translator");

        if (hlsl is not null && translator is not null)
        {
            throw new ArgumentException("--hlsl and --translator name two candidates; give one");
        }

        if (hlsl is not null)
        {
            return ReplayCandidate.FromHlsl(folder, capture, hlsl, ShaderTools.Backend(arguments, retry: false));
        }

        if (translator is null)
        {
            return ReplayCandidate.Captured(folder, capture);
        }

        if (translator != CurrentTranslator)
        {
            throw new ArgumentException($"--translator {translator}: only 'current' (this build's translator)");
        }

        return ReplayCandidate.FromTranslator(folder, capture, ShaderTools.Backend(arguments, retry: false));
    }

    /// <summary>WARP, the first hardware adapter, or the adapter of that DXGI index.</summary>
    private static GpuDevice Device(string adapter, bool debugLayer)
    {
        if (adapter == Warp)
        {
            return GpuDevice.CreateWarp(debugLayer);
        }

        if (adapter == Hardware)
        {
            return new GpuDevice(0, debugLayer);
        }

        return new GpuDevice(int.Parse(adapter, CultureInfo.InvariantCulture), debugLayer);
    }

    private static float? Tolerance(string? text)
    {
        if (text is null)
        {
            return null;
        }

        return float.Parse(text, CultureInfo.InvariantCulture);
    }

    private static void Print(DispatchCapture capture, ReplayCandidate candidate, GpuDevice device, ReplayReport report)
    {
        var compile = candidate.CompileMilliseconds is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $", compiled in {candidate.CompileMilliseconds:F0} ms");
        Console.WriteLine($"{capture.Program.Name} on {device.AdapterName}: {candidate.Source}, DXIL {candidate.Dxil.Length} bytes{compile}");
        var times = string.Join(" ", report.Outcome.Milliseconds.Select(time => time.ToString("F3", CultureInfo.InvariantCulture)));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"runs {report.Outcome.Milliseconds.Count}: median {report.Outcome.MedianMilliseconds:F3} ms ({times})"));

        foreach (var difference in report.Differences)
        {
            if (difference.Same)
            {
                Console.WriteLine($"  {difference.Resource}: same ({difference.Compared} bytes)");

                continue;
            }

            var offsets = string.Join(", ", difference.FirstOffsets.Select(offset => $"0x{offset:x}"));
            Console.WriteLine($"  {difference.Resource}: {difference.Differing} dwords differ of {difference.Compared} bytes, first at {offsets}");
        }

        foreach (var error in report.DebugErrors)
        {
            Console.WriteLine($"  debug layer: {error}");
        }
    }
}
