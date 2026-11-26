// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Globalization;
using FiveStar.Shaders.Containers;
using FiveStar.Shaders.Disassembly;
using FiveStar.Shaders.Hardware;
using FiveStar.Shaders.Tests.Toolchain;
using FiveStar.Shaders.Tests.Warp;
using FiveStar.Shaders.Translation;

namespace FiveStar.Shaders.Tests.Cases;

/// <summary>
/// Runs a pixel-shader case: appends the export of v20.. (mrt0.., uint4 SV_Target signature so the targets are
/// bit-exact), assembles, translates, compiles (ps_6_7, with dxc or with the DXIL backend), draws on 8x8 and lists every
/// pixel that differs.
/// </summary>
public sealed class PixelCaseRunner
{
    private const int FirstResult = 20;
    private const int WaveSize = 32;

    private readonly WarpFixture _fixture;

    public PixelCaseRunner(WarpFixture fixture)
    {
        _fixture = fixture;
    }

    public IReadOnlyList<string> Run(PixelCase testCase, bool direct = false)
    {
        return Compare(testCase, Draw(testCase, direct));
    }

    /// <summary>Draws the case and returns what the draw left (targets and, with a pixel block, depth).</summary>
    public WarpResult Draw(PixelCase testCase, bool direct = false)
    {
        var pixel = Build(testCase, direct);
        var helper = testCase.HalfCoverage ? "half_vs.hlsl" : "fullscreen_vs.hlsl";

        if (testCase.DbShaderControl is not null)
        {
            helper = "depth_vs.hlsl";
        }

        var vertex = _fixture.Helper(helper, "vs");
        var framebuffer = Framebuffer.Fullscreen(testCase.Targets, PixelCase.Size, PixelCase.Size, testCase.FloatTargets) with { Depth = testCase.DbShaderControl is not null };

        return _fixture.Runner.Graphics(new GraphicsJob(vertex, pixel, framebuffer, testCase.Bindings));
    }

    /// <summary>The translated HLSL of a case.</summary>
    public string Translate(PixelCase testCase)
    {
        return ShaderWorkbench.Translate(Program(testCase));
    }

    /// <summary>The export lines: v20.. padded with off to whole mrts, done vm on the last.</summary>
    public static IEnumerable<string> ExportLines(int outputs)
    {
        var registers = Enumerable.Range(FirstResult, outputs).Select(register => $"v{register}").ToList();

        while (registers.Count % 4 != 0)
        {
            registers.Add("off");
        }

        for (var target = 0; target < registers.Count / 4; target++)
        {
            var last = target == (registers.Count / 4) - 1 ? " done vm" : string.Empty;

            yield return $"exp mrt{target} {string.Join(", ", registers.Skip(4 * target).Take(4))}{last}";
        }
    }

    /// <summary>The case assembled, patched and set up as a pixel program (uint4 targets unless FloatTargets).</summary>
    private ShaderProgram Program(PixelCase testCase)
    {
        var exports = string.Join("\n", ExportLines(testCase.Outputs));
        var source = testCase.Raw ? testCase.Program.Replace("{EXPORT}", exports, StringComparison.Ordinal) : $"{testCase.Program}\n{exports}\ns_endpgm";
        var assembled = _fixture.Workbench.Assemble(testCase.Name, source, WaveSize);
        var signatures = ShaderSignatures.None;

        if (!testCase.FloatTargets)
        {
            var targets = Enumerable.Range(0, testCase.Targets).Select(index => new SignatureElement("SV_Target", index, "SV_Target", "uint", index, "xyzw", 0xF, 0));
            signatures = new ShaderSignatures([], [.. targets]);
        }

        var config = new ShaderConfig { Stage = HardwareStage.Pixel, WaveSize = WaveSize, Pixel = PixelBlockOf(testCase.DbShaderControl) };
        var (instructions, code) = Patched(testCase, assembled.Instructions, assembled.Code);

        return new ShaderProgram(ShaderStage.Pixel, instructions, code, signatures, config, null);
    }

    /// <summary>A pixel block with the default input layout (persp_center, pos_x..pos_w) and the given DB_SHADER_CONTROL.</summary>
    private static PixelConfig? PixelBlockOf(uint? control)
    {
        const int InputEnableField = 0x248;
        const int ControlField = 0x24C;
        const int MaskField = 0x250;
        const int BlockBytes = 0x300;
        const uint DefaultInputs = 0xF02;
        const int UnusedPrimMaskSgpr = 16;

        if (control is null)
        {
            return null;
        }

        var block = new byte[BlockBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(InputEnableField), DefaultInputs);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(ControlField), control.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(MaskField), 0xF);

        return PixelBlock.Decode(block, UnusedPrimMaskSgpr);
    }

    private byte[] Build(PixelCase testCase, bool direct)
    {
        var program = Program(testCase);
        var hlsl = testCase.TypedViews ? ShaderWorkbench.TranslateTypedViews(program) : ShaderWorkbench.Translate(program);

        if (direct)
        {
            return _fixture.Workbench.Compile(testCase.Name, hlsl, "ps", direct: true);
        }

        return _fixture.Workbench.Compile(testCase.Name, hlsl, "ps");
    }

    /// <summary>
    /// The code with <see cref="PixelCase.ExportPatch"/> applied through an FSSM patch table (ExportPatches) at the first
    /// <c>s_nop</c>, decoded again; the assembled program when the case has none.
    /// </summary>
    private static (IReadOnlyList<Instruction> Instructions, byte[] Code) Patched(PixelCase testCase, IReadOnlyList<Instruction> instructions, byte[] code)
    {
        if (testCase.ExportPatch is null)
        {
            return (instructions, code);
        }

        const int SizeField = 0x25C;
        const int PatchesField = 0x260;
        const int PatchBytes = 4 + (10 * 10 * 4);
        const int FullSlot = 4 + (9 * 10 * 4);
        const int HashPrefix = 0x18;

        var table = new byte[PatchesField + (8 * PatchBytes)];
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(SizeField), (uint)testCase.ExportPatch.Length);

        for (int target = 0; target < 8; target++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(PatchesField + (target * PatchBytes)), uint.MaxValue);
        }

        int start = instructions.First(instruction => instruction.Mnemonic == "s_nop").Address;
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(PatchesField), (uint)((start + HashPrefix) / 4));

        for (int i = 0; i < testCase.ExportPatch.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(PatchesField + FullSlot + (4 * i)), testCase.ExportPatch[i]);
        }

        var patched = ExportPatches.Apply(table, code);

        return (new RdnaDecoder().Disassemble(patched, WaveSize), patched);
    }

    private List<string> Compare(PixelCase testCase, WarpResult result)
    {
        var mismatches = new List<string>();
        var clear = testCase.FloatTargets ? Framebuffer.ClearFloatBits : Framebuffer.ClearUint;

        var pixels = testCase.HalfCoverage ? _fixture.HalfCoverageLanes : _fixture.PixelLanes;

        foreach (var pixel in pixels)
        {
            var expected = testCase.Expect(pixel);
            var actual = Enumerable.Range(0, testCase.Targets).SelectMany(target => result.Pixel(target, pixel.X, pixel.Y)).Take(testCase.Outputs).ToArray();

            if (testCase.HalfCoverage && pixel.X + pixel.Y > 6)
            {
                expected = null;
            }

            if (expected is null)
            {
                if (actual.Any(word => word != clear))
                {
                    mismatches.Add(string.Create(CultureInfo.InvariantCulture, $"({pixel.X},{pixel.Y}) expected discarded, actual {Words(actual)}"));
                }

                continue;
            }

            for (var index = 0; index < expected.Length; index++)
            {
                if (!expected[index].Matches(actual[index]))
                {
                    mismatches.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"({pixel.X},{pixel.Y}) v{FirstResult + index} expected {expected[index]} actual {Expected.Hex(actual[index])}"));
                }
            }
        }

        return mismatches;
    }

    private static string Words(uint[] words)
    {
        return string.Join(" ", words.Select(Expected.Hex));
    }
}
