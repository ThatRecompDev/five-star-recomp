// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Text.RegularExpressions;
using FiveStar.Shaders.Translation.Core;
using FiveStar.Shaders.Translation.Waves;

namespace FiveStar.Shaders.Translation.Stages;

/// <summary>
/// Assembles the HLSL program from the translated blocks: includes, input/output structs, resource declarations,
/// the entry point with its register variables, the prologue and the block dispatch, then the output mapping.
/// </summary>
internal sealed partial class ProgramEmitter
{
    /// <summary>The entry line of a geometry program, filled in by the geometry builder.</summary>
    public const string GeometryEntryPlaceholder = "/*GS_MAIN*/";

    private const string OutStruct = "struct Out {";
    private const string SuperstepHeader = "rdna_superstep.hlsli";
    private const string SharedExponentHeader = "rdna_shared_exponent.hlsli";
    private const string LiveLanesHeader = "rdna_live_lanes.hlsli";
    private const int DefaultComputeThreads = 64;
    private const int BytesPerDword = 4;
    private const int CodeLoadLine = 2;

    // Compute programs without an LDS size in the header get the whole 64 KB.
    private const int DefaultLdsBytes = 65536;

    private readonly TranslationState _state;
    private readonly OutputWriter _outputs;
    private readonly MeshDeclarations _mesh;
    private readonly BodyWriter _body;
    private bool _codeLanesFromCode;

    public ProgramEmitter(TranslationState state, OutputWriter outputs, MeshDeclarations mesh, BodyWriter body)
    {
        _state = state;
        _outputs = outputs;
        _mesh = mesh;
        _body = body;
    }

    public string Emit()
    {
        var lines = Header();

        if (_state.Stage == ShaderStage.Pixel)
        {
            new PixelInputWriter(_state).Write(lines);
        }

        _outputs.Write(lines);
        InsertFetchInputs(lines);
        var entry = EntrySignature(lines);
        InsertResources(lines);

        lines.Add(entry + " {");
        _body.Write(lines);
        ReadLiveLanes(lines);
        var text = string.Join("\n", lines) + "\n";

        if (_codeLanesFromCode)
        {
            // No table to index: the loads stay as they were, the program byte for byte as before.
            text = text.Replace("CodeLoadLane(", "CodeLoad(", StringComparison.Ordinal);
        }

        return Finish(text);
    }

    private List<string> Header()
    {
        const string Include = "#include \"rdna_prelude.hlsli\"";

        var lines = new List<string> { Include };

        if (_state.EmulateWaves)
        {
            // One entry, so line positions after the include stay as they are.
            lines[0] = string.Join("\n", WaveDefines()) + "\n" + Include;
        }

        if (_state.Supersteps)
        {
            // Written into the program, not included: the headers every program includes stay as they are.
            lines[0] += "\n" + RuntimeIncludes.Read(SuperstepHeader).TrimEnd('\n');
        }

        if (_state.UsesSharedExponent)
        {
            // Written into the listed programs only (TranslationOptions.SharedExponentPrograms), as the superstep header.
            lines[0] += "\n" + RuntimeIncludes.Read(SharedExponentHeader).TrimEnd('\n');
        }

        if (_state.UsesBvh)
        {
            lines.Add("#include \"rdna_bvh.hlsli\"");
        }

        lines.AddRange(_state.StaticSamplers.Select(sampler => sampler.Line));

        lines.Add(string.Empty);

        return lines;
    }

    /// <summary>The prelude switches for wave emulation and the program's wave floor (see WaveOps.FloorLine).</summary>
    private List<string> WaveDefines()
    {
        var wave = _state.WaveSize;
        var floor = Math.Max(_state.LaneReach, WaveOps.NarrowestHardwareWave);
        var lines = new List<string> { $"#define VW_WAVE {wave}", "#define VW_EMULATE 1" };

        if (_state.Supersteps)
        {
            // Every wave operation exchanges through groupshared memory; quads stay native.
            lines.AddRange(["#define VW_GROUP_LANES 1", $"#define VW_GROUP_THREADS {_state.GroupThreads}u"]);

            if (_state.SuperstepValues)
            {
                lines.Add($"#define VW_SS_VALUES {SuperstepSplitter.FirstLaneValues}");
            }

            lines.Insert(0, WaveOps.FloorLine(wave, WaveOps.NarrowestHardwareWave));

            return lines;
        }

        if (_state.Stage is ShaderStage.Pixel or ShaderStage.Vertex && floor > WaveOps.NarrowestHardwareWave)
        {
            // Lanes a narrower wave lacks are console lanes without a pixel or vertex; reads of them may hold.
            var analysis = new MissingLaneAnalysis(_state.Blocks, _state.Prologue, wave);
            floor = analysis.Floor(floor);
            analysis.Rewrite(_state.Blocks);

            if (analysis.Reason is not null)
            {
                lines.Insert(0, WaveOps.NarrowerLine(analysis.Reason));
            }
        }

        if (_state.Stage == ShaderStage.Mesh)
        {
            lines.Add("#define VW_GROUP_LANES 1");
            floor = MeshFloor(lines, wave, floor);
        }

        lines.Insert(0, WaveOps.FloorLine(wave, floor));

        return lines;
    }

    /// <summary>
    /// A mesh program's floor: the other lanes of its wave are live threads of other hardware waves (see WindowAnalysis),
    /// so it needs its whole wave unless the analysis shows otherwise, or fast waves take it approximately.
    /// </summary>
    private int MeshFloor(List<string> lines, int wave, int floor)
    {
        var reason = new WindowAnalysis(_state.Blocks, _state.Prologue, _state.MeshThreads).Check();

        if (reason is null)
        {
            return floor;
        }

        if (_state.FastWaves)
        {
            var fast = new WindowAnalysis(_state.Blocks, _state.Prologue, _state.MeshThreads) { FastWaves = true };

            if (fast.Check() is null)
            {
                lines.Insert(0, WaveOps.FastLine(reason));
                EnterAlways(fast.EnteredAlways);

                return floor;
            }
        }

        lines.Insert(0, WaveOps.NarrowerLine(reason));

        return wave;
    }

    /// <summary>Fast waves: the skips every window takes become jumps into the path they skip (WindowAnalysis.EntersAlways).</summary>
    private void EnterAlways(IReadOnlyDictionary<int, int> paths)
    {
        for (var index = 0; index < _state.Blocks.Count; index++)
        {
            var block = _state.Blocks[index];

            if (!paths.TryGetValue(block.Address, out var path))
            {
                continue;
            }

            var lines = new List<string>(block.Lines);
            var branch = lines.FindLastIndex(line => BranchPattern().IsMatch(line));

            if (branch >= 0)
            {
                lines[branch] = $"  pc = {Hlsl.Hex(path)};";
            }

            _state.Blocks[index] = new CodeBlock(block.Address, lines, new BlockExit(BlockExitKind.Jump, path, null, null));
        }
    }

    /// <summary>Vertex attributes the driver's fetch shader would load (table @0x138); the pipeline binds ATTR&lt;n&gt;.</summary>
    private void InsertFetchInputs(List<string> lines)
    {
        if (_state.FetchInputs.Count == 0)
        {
            return;
        }

        var members = _state.FetchInputs.OrderBy(input => input.Attribute).ThenBy(input => input.Components)
            .Select(input => $"  float4 a{input.Attribute} : ATTR{input.Attribute};");
        lines.Insert(lines.IndexOf(OutStruct), "struct FetchIn {\n" + string.Join("\n", members) + "\n};");
    }

    private string EntrySignature(List<string> lines)
    {
        switch (_state.Stage)
        {
            case ShaderStage.Vertex:
                return VertexEntry();
            case ShaderStage.Mesh:
                return MeshEntry(lines);
            case ShaderStage.Geometry:
                return GeometryEntryPlaceholder;
            case ShaderStage.Compute:
                return ComputeEntry();
            default:
                return PixelEntry();
        }
    }

    private string VertexEntry()
    {
        var fetch = _state.FetchInputs.Count > 0 ? ", FetchIn fin" : string.Empty;

        return $"Out main(uint in_vid : SV_VertexID, uint in_iid : SV_InstanceID{fetch})";
    }

    private string MeshEntry(List<string> lines)
    {
        var declarations = _state.MeshYield ? MeshDeclarations.DirectDeclarations() : _mesh.StagingDeclarations(_outputs.OutputRegisters());
        lines.Insert(lines.IndexOf(OutStruct), declarations + _mesh.FirstLaneDeclarations());

        if (!_state.Outputs.Contains("pos0"))
        {
            throw new UnsupportedProgramException("mesh shader without position export");
        }

        if (_state.MeshYield)
        {
            _mesh.ExpandDirectExports();
        }

        return _mesh.EntrySignature();
    }

    private string ComputeEntry()
    {
        var threads = _state.Config.Compute?.NumThreads;
        var (x, y, z) = (DefaultComputeThreads, 1, 1);

        if (threads is not null)
        {
            (x, y, z) = (threads.Value.X, threads.Value.Y, threads.Value.Z);
        }

        return $"[numthreads({x}, {y}, {z})]\nvoid main(uint3 in_gid : SV_GroupID, uint3 in_gtid : SV_GroupThreadID, uint in_gi : SV_GroupIndex)";
    }

    private string PixelEntry()
    {
        var used = _state.Inputs.Values.ToHashSet();
        var parameters = new List<string>();

        if (_state.Attributes.Count > 0)
        {
            parameters.Add("PSIn pin");
        }

        if (_state.Outputs.Contains("mrtz") && _outputs.ConservativeDepth)
        {
            // A conservative depth output needs the centroid position (dxc's validator); without MSAA, or on a pixel the
            // primitive covers whole, it is the pixel center the console reads.
            parameters.Add("noperspective centroid float4 in_pos : SV_Position");
        }
        else if (used.Overlaps(["pos_x", "pos_y", "pos_z", "pos_w", "pos"]))
        {
            parameters.Add("float4 in_pos : SV_Position");
        }

        (string Input, string Parameter)[] system =
        [
            ("front_face", "bool in_front : SV_IsFrontFace"),
            ("ancillary", "uint in_sample : SV_SampleIndex"),
            ("sample_coverage", "uint in_coverage : SV_Coverage"),
            ("bary", "float3 in_bary : SV_Barycentrics"),
        ];
        parameters.AddRange(system.Where(entry => used.Contains(entry.Input)).Select(entry => entry.Parameter));
        var signature = $"Out main({string.Join(", ", parameters)})";

        if (DepthBeforeShader())
        {
            return "[earlydepthstencil]\n" + signature;
        }

        return signature;
    }

    /// <summary>
    /// DB_SHADER_CONTROL's DEPTH_BEFORE_SHADER (bit 12) without a depth export: the console tests and writes depth before
    /// the shader runs, whatever it kills or stores, as [earlydepthstencil] does. Without it a PC driver tests a shader
    /// that kills or writes UAVs after it runs: it runs for hidden pixels, their UAV writes land and a killed pixel's
    /// depth is not written (3,028 of the 15,354 pixel programs of the corpus set it, 2,422 of them with KILL_ENABLE).
    /// </summary>
    private bool DepthBeforeShader()
    {
        const int DepthBeforeShaderBit = 12;
        var pixel = _state.Config.Pixel;

        if (pixel is null || _state.Outputs.Contains("mrtz"))
        {
            return false;
        }

        return ((pixel.DbShaderControl >> DepthBeforeShaderBit) & 1) != 0;
    }

    private void InsertResources(List<string> lines)
    {
        if (_state.UsesLds && _state.Stage != ShaderStage.Geometry && !_state.IsCopyShader)
        {
            // Geometry programs use a private static array, declared by the geometry builder.
            var bytes = _state.Config.LdsBytes ?? 0;

            if (bytes == 0)
            {
                bytes = DefaultLdsBytes;
            }

            var dwords = Math.Max(1, bytes / BytesPerDword);
            lines.Insert(1, $"groupshared uint g_lds[{dwords}];");
        }

        if (_state.UsesCode)
        {
            var code = _state.Code ?? [];
            var words = Enumerable.Range(0, code.Length / BytesPerDword)
                .Select(index => Hlsl.Hex(BinaryPrimitives.ReadUInt32LittleEndian(code.AsSpan(index * BytesPerDword)))).ToList();
            lines.Insert(1, $"static const uint g_code[{words.Count}] = {{{string.Join(",", words)}}};");
            lines.Insert(CodeLoadLine, $"uint CodeLoad(uint a) {{ return g_code[min(a >> 2, {words.Count - 1}u)]; }}");

            if (_state.UsesCodeLanes)
            {
                lines.InsertRange(CodeLoadLine + 1, LaneLoad(words));
            }
        }
    }

    /// <summary>
    /// CodeLoadLane, a load at a per-lane address: from the code words of the tables the s_getpc pointers point to
    /// (g_data, <see cref="CodeData"/>). Indexed per lane, the whole code array cost a driver 26 s and 1.9 GB to compile,
    /// its tables alone 14 s and 0.5 GB. None when the tables are unknown: the loads then read CodeLoad.
    /// </summary>
    private List<string> LaneLoad(List<string> words)
    {
        int? start = CodeData.Start(_state.Translated);

        if (start is null || start.Value / BytesPerDword >= words.Count)
        {
            _codeLanesFromCode = true;

            return [];
        }

        var data = words.Skip(start.Value / BytesPerDword).ToList();
        string from = Hlsl.HexUint((uint)start.Value);

        return
        [
            $"static const uint g_data[{data.Count}] = {{{string.Join(",", data)}}};   // code words from {Hlsl.Hex(start.Value)}",
            $"uint CodeLoadLane(uint a) {{ return g_data[min(a >= {from} ? (a - {from}) >> 2 : 0u, {data.Count - 1}u)]; }}",
        ];
    }

    /// <summary>Program-wide rewrites: exact f16 rounding, helper lanes and rasterizer-ordered views for pixel shaders.</summary>
    /// <summary>
    /// A pixel program's cross-lane reads as reads of running lanes only (rdna_live_lanes.hlsli, written into the
    /// program): a pixel wave the desktop fills partly leaves lanes unlaunched, which the console runs with what the
    /// program gives a lane without a pixel. A compute group whose size is no multiple of 32 (avgLuminanceCompute's
    /// one-thread LuminanceFromHDRBinkCS) takes them too; other groups fill their waves and keep <c>vw_read</c> as it is.
    /// </summary>
    private void ReadLiveLanes(List<string> lines)
    {
        // A compute group whose size is no multiple of 32 leaves a part-full last hardware wave as well.
        const int FullWave = 32;

        if (_state.Supersteps || (_state.Stage != ShaderStage.Pixel && !(_state.Stage == ShaderStage.Compute && _state.GroupThreads % FullWave != 0)))
        {
            return;
        }

        bool reads = false;

        for (int i = 1; i < lines.Count; i++)
        {
            if (!lines[i].Contains("vw_read", StringComparison.Ordinal))
            {
                continue;
            }

            lines[i] = lines[i].Replace("vw_read_fill(", "vw_read_fill_live(", StringComparison.Ordinal).Replace("vw_read(", "vw_read_live(", StringComparison.Ordinal);
            reads = true;
        }

        if (reads)
        {
            lines[0] += "\n" + RuntimeIncludes.Read(LiveLanesHeader).TrimEnd('\n');
        }
    }

    private string Finish(string text)
    {
        // AMD rounds f32 to f16 to nearest even; HLSL f32tof16 rounding is implementation-defined.
        text = F32ToF16Pattern().Replace(text, "f32tof16_rne(");
        text = FormatLoadForm(text);
        text = ScalarLoadForm(text);

        if (_state.Stage != ShaderStage.Pixel)
        {
            return text;
        }

        // EXEC starts with live pixels only; wave operations must still see helper lanes (needs ps_6_7).
        text = text.Replace("bool active = true", "bool active = !IsHelperLane()", StringComparison.Ordinal);
        text = WholeQuadEntry(text);
        text = ActiveRuns.Branch(text);

        if (_state.Pops || (_state.Config.Pixel?.Pops ?? false))
        {
            // Primitive-ordered pixel shading: every writable resource becomes a rasterizer-ordered view.
            text = WritableViewPattern().Replace(text, "RasterizerOrdered$1");
        }

        return text.Replace("\nOut main(", "\n[WaveOpsIncludeHelperLanes]\nOut main(", StringComparison.Ordinal);
    }

    /// <summary>
    /// The most formatted loads a program may hold and keep FmtLoad's fast path for 32-bit components (a branch, so a
    /// few blocks at every inlined call). indirect_diffuse_lighting~CS_ScreenProbes_ActivateProbes 9083c094 holds 3,281:
    /// the fast path took its DXIL from 33,535 blocks and 561,823 instructions to 49,940 and 807,237, and the driver's
    /// compile of it took 11.5-12.8 GB (a warm-up stopped at the memory guard). The light proxy holds 113.
    /// </summary>
    public const int FormatLoadLimit = 512;

    /// <summary>The program text, with the prelude's formatted loads decode-only past <see cref="FormatLoadLimit"/>.</summary>
    internal static string FormatLoadForm(string text)
    {
        const string Include = "#include \"rdna_prelude.hlsli\"";
        int loads = FormatLoadPattern().Count(text);
        int at = text.IndexOf(Include, StringComparison.Ordinal);

        if (loads <= FormatLoadLimit || at < 0)
        {
            return text;
        }

        return text.Insert(at, "#define FIVESTAR_FMT_DECODE_ONLY 1\n");
    }

    [GeneratedRegex(@"\bFmtLoad\(")]
    private static partial Regex FormatLoadPattern();

    /// <summary>
    /// The most dwords a program may read through shared s_load views (prelude sptr_views, SPtrLoadAt) and keep them.
    /// A view walks two pages per load and picks each dword's page with selects: in programs with thousands of s_load
    /// dwords that is more code than a walk per dword (the RaytracingResolve programs, 3,300 dwords each, grew 10% over
    /// batch 1, indirect_diffuse_lighting~CS_ScreenProbes_ActivateRays 16%), and the driver's compile memory follows the
    /// code. The deferred G-buffer pixel programs read 600 or fewer and keep the shared walk.
    /// </summary>
    public const int SharedScalarLoadLimit = 1024;

    /// <summary>The program text, each s_load dword read through the map on its own past <see cref="SharedScalarLoadLimit"/>.</summary>
    internal static string ScalarLoadForm(string text)
    {
        if (SharedLoadPattern().Count(text) <= SharedScalarLoadLimit)
        {
            return text;
        }

        // SPtrLoadAt(views, delta, lo, hi, off) reads the dword SPtrLoad(lo, hi, off) reads.
        text = SharedLoadPattern().Replace(text, "SPtrLoad(");

        return SharedViewPattern().Replace(text, string.Empty);
    }

    [GeneratedRegex(@"\bSPtrLoadAt\(_t\d+, \d+u, ")]
    private static partial Regex SharedLoadPattern();

    [GeneratedRegex(@"^[ ]*(?:uint4 )?_t\d+ = sptr_views\([^;\n]*\);\n", RegexOptions.Multiline)]
    private static partial Regex SharedViewPattern();

    /// <summary>
    /// A pixel program's s_wqm of its initial EXEC (the live pixels), straight from the entry with no branch, discard or
    /// other EXEC write before it, sets every lane the GPU runs: a quad runs only with a covered pixel, its other lanes as
    /// helpers. So that EXEC is <c>true</c>, and the compiler folds the select of each vector write under it (and of the
    /// writes after a restore of a mask saved from it) instead of keeping a quad reduction it cannot see through:
    /// 350 of PS_DeferredTextured_c5fd36bb 07a9003b's 515 selects are straight after it.
    /// </summary>
    internal static string WholeQuadEntry(string text)
    {
        var start = text.IndexOf("\nOut main(", StringComparison.Ordinal);

        if (start < 0)
        {
            return text;
        }

        var body = text.IndexOf('\n', start + 1) + 1;
        var line = $"\n  active = {Emitters.SaluEmitter.QuadMask("active")};\n";
        var at = text.IndexOf(line, body - 1, StringComparison.Ordinal);

        if (at < 0 || !StraightLine(OnceWrapper(text, text[body..(at + 1)])))
        {
            return text;
        }

        return text[..at] + "\n  active = true;\n" + text[(at + line.Length)..];
    }

    /// <summary>
    /// The entry code without the line opening the body's <c>do { ... } while (false);</c> (a program that ends early
    /// breaks out of it), which runs once: only when the first loop end at the body's level is that one.
    /// </summary>
    private static string OnceWrapper(string text, string prefix)
    {
        const string Open = "\n  do {\n";

        if (!prefix.StartsWith(Open[1..], StringComparison.Ordinal) && !prefix.Contains(Open, StringComparison.Ordinal))
        {
            return prefix;
        }

        var end = BodyLoopEndPattern().Match(text);

        if (!end.Success || end.Value.Trim() != "} while (false);")
        {
            return prefix;
        }

        return ("\n" + prefix).Replace(Open, "\n", StringComparison.Ordinal);
    }

    /// <summary>Whether entry code runs every line in order and leaves EXEC alone: no block, branch, discard or EXEC write.</summary>
    private static bool StraightLine(string prefix)
    {
        if (prefix.IndexOfAny(['{', '}']) >= 0 || ExecWritePattern().IsMatch(prefix))
        {
            return false;
        }

        foreach (var word in (string[])["if (", "for (", "while (", "switch (", "discard", "return"])
        {
            if (prefix.Contains(word, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex(@"^  \} while \(.*$", RegexOptions.Multiline)]
    private static partial Regex BodyLoopEndPattern();

    [GeneratedRegex(@"^\s*active = ", RegexOptions.Multiline)]
    private static partial Regex ExecWritePattern();

    [GeneratedRegex(@"^\s*pc = \(.*\) \? 0x[0-9a-f]+ : 0x[0-9a-f]+;\s*$")]
    private static partial Regex BranchPattern();

    [GeneratedRegex(@"\bf32tof16\(")]
    private static partial Regex F32ToF16Pattern();

    [GeneratedRegex(@"\bRW(Texture[123]D(?:Array)?|ByteAddressBuffer|Buffer|StructuredBuffer)\b")]
    private static partial Regex WritableViewPattern();
}
