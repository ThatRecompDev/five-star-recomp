// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.RegularExpressions;
using FiveStar.Shaders.Containers;
using FiveStar.Shaders.Hardware;
using FiveStar.Shaders.Tests.Reference;
using FiveStar.Shaders.Tests.Toolchain;
using FiveStar.Shaders.Tests.Warp;
using FiveStar.Shaders.Translation;

namespace FiveStar.Shaders.Tests.Cases;

/// <summary>
/// A wave64 NGG program through the translator's two-phase mesh path: GS_ALLOC_REQ (m0 = 16 primitives &lt;&lt; 12 | 18
/// vertices), s_barrier, exp pos0 / param0 from lanes below the vertex count, exp prim (three 10-bit indices, bit 31
/// culls) from lanes below the primitive count: 8 horizontal strips of 2 triangles, primitive 5 culled. Drawn natively
/// (DispatchMesh) or through the mesh-to-compute fallback; the image must match a rasterizer model (flat attributes
/// from the provoking vertex) either way. The thread id makes a round trip through ds_bpermute (read from lane id ^ 1,
/// then ^ 1 again), which needs console lane numbers on WARP's 4-lane waves.
/// </summary>
public sealed partial class MeshStageRunner
{
    public const string Native = "ms_ngg_strips";
    public const string FallbackTable = "msfb_ngg_strips";
    public const string FallbackRoot = "msfb_ngg_strips_rootdesc";
    public const string FallbackOverflow = "msfb_overflow_dropped";

    /// <summary>
    /// The program without the ds_bpermute round trip, with an SGPR the vertex region writes but nothing reads after
    /// it, and the culled primitive's index taken with v_readfirstlane inside the primitive region: WARP's 4-lane waves
    /// above the vertex and primitive counts skip those regions while their console wave runs them.
    /// </summary>
    public const string Windows = "ms_ngg_windows";

    /// <summary>The program in the superstep form (HlslTranslator.SuperstepVariant): its barrier and both phases as steps.</summary>
    public const string Supersteps = "ms_ngg_supersteps";

    /// <summary>The superstep form through the mesh-to-compute fallback (GPUs without mesh shaders and narrow waves).</summary>
    public const string SuperstepFallback = "msfb_ngg_supersteps";

    private const string RoundTrip = """
        v_xor_b32_e32 v30, 1, v1
        v_lshlrev_b32_e32 v30, 2, v30
        ds_bpermute_b32 v31, v30, v1
        s_waitcnt lgkmcnt(0)
        v_xor_b32_e32 v1, 1, v31
        """;

    private const string VertexRegion = "s_cbranch_execz .Lprim";
    private const string PrimitiveRegion = "s_cbranch_execz .Lend";

    private const int Vertices = 18;
    private const int Primitives = 16;
    private const int Culled = 5;
    private const double AttributeBase = 100.0;
    private const int Size = 8;
    private const int MaxOutputs = 64;

    private const string Program = """
        s_mov_b32 m0, s8
        s_sendmsg sendmsg(MSG_GS_ALLOC_REQ)
        v_mbcnt_lo_u32_b32 v1, -1, 0
        v_mbcnt_hi_u32_b32 v1, -1, v1
        s_bfe_u32 s20, s3, 0x40018
        s_lshl_b32 s20, s20, 6
        v_add_nc_u32_e32 v1, s20, v1
        v_xor_b32_e32 v30, 1, v1
        v_lshlrev_b32_e32 v30, 2, v30
        ds_bpermute_b32 v31, v30, v1
        s_waitcnt lgkmcnt(0)
        v_xor_b32_e32 v1, 1, v31
        s_barrier
        s_and_b32 s21, s8, 0xfff
        s_mov_b64 s[24:25], exec
        v_cmp_gt_u32_e32 vcc, s21, v1
        s_and_saveexec_b64 s[22:23], vcc
        s_cbranch_execz .Lprim
        v_and_b32_e32 v2, 1, v1
        v_cvt_f32_u32_e32 v2, v2
        v_mul_f32_e32 v2, 2.0, v2
        v_add_f32_e32 v2, -1.0, v2
        v_lshrrev_b32_e32 v3, 1, v1
        v_cvt_f32_u32_e32 v3, v3
        v_mul_f32_e32 v3, 0x3e800000, v3
        v_sub_f32_e32 v3, 1.0, v3
        v_mov_b32 v4, 0
        v_mov_b32 v5, 1.0
        exp pos0 v2, v3, v4, v5 done
        v_cvt_f32_u32_e32 v6, v1
        v_lshrrev_b32_e32 v7, 1, v1
        v_cvt_f32_u32_e32 v7, v7
        v_add_f32_e32 v8, s10, v6
        v_mov_b32 v9, 0.5
        exp param0 v6, v7, v8, v9
        .Lprim:
        s_mov_b64 exec, s[24:25]
        s_lshr_b32 s21, s8, 12
        s_and_b32 s21, s21, 0xfff
        v_cmp_gt_u32_e32 vcc, s21, v1
        s_and_saveexec_b64 s[22:23], vcc
        s_cbranch_execz .Lend
        v_lshrrev_b32_e32 v10, 1, v1
        v_lshlrev_b32_e32 v10, 1, v10
        v_and_b32_e32 v11, 1, v1
        v_add_nc_u32_e32 v12, v10, v11
        v_lshl_add_u32 v13, v11, 1, v10
        v_add_nc_u32_e32 v13, 1, v13
        v_add_nc_u32_e32 v14, 2, v10
        v_lshlrev_b32_e32 v13, 10, v13
        v_lshlrev_b32_e32 v14, 20, v14
        v_or_b32_e32 v15, v12, v13
        v_or_b32_e32 v15, v15, v14
        v_cmp_eq_u32_e64 s[26:27], s9, v1
        v_cndmask_b32_e64 v16, 0, 0x80000000, s[26:27]
        v_or_b32_e32 v15, v15, v16
        exp prim v15, off, off, off done
        .Lend:
        s_endpgm
        """;

    private readonly WarpFixture _fixture;

    public MeshStageRunner(WarpFixture fixture)
    {
        _fixture = fixture;
    }

    public static IReadOnlyList<string> Variants => [Native, FallbackTable, FallbackRoot, FallbackOverflow, Supersteps, SuperstepFallback];

    /// <summary>Draws a variant; <paramref name="direct"/> compiles a native mesh shader with the DXIL backend.</summary>
    public IReadOnlyList<string> Run(string variant, bool direct = false)
    {
        var mesh = Translate(variant);
        var (pixelSource, _) = PassThroughPixelShader.Build(mesh, ["param0"], flat: true);
        var pixel = _fixture.Workbench.Compile(variant + "_ps", pixelSource, "ps");
        var framebuffer = Framebuffer.Exact(1, Size, Size);
        var bindings = new Bindings { UserSgprs = [(Primitives << 12) | Vertices, Culled, FloatBits.Of(AttributeBase)] };
        var image = Reference();

        if (variant is Native or Windows or Supersteps)
        {
            var result = _fixture.Runner.Mesh(new MeshJob(_fixture.Workbench.Compile(variant, mesh, "ms", direct), pixel, framebuffer, bindings, 1));

            return ImageCheck.Compare(result, Size, Size, (x, y, _) => image[y, x]);
        }

        return RunFallback(variant, mesh, pixel, framebuffer, bindings, image);
    }

    private List<string> RunFallback(string variant, string mesh, byte[] pixel, Framebuffer framebuffer, Bindings bindings, Expected[]?[,] image)
    {
        var (compute, vertex) = MeshComputeFallback.Rewrite(mesh);
        var stride = int.Parse(StridePattern().Match(compute).Groups[1].Value, CultureInfo.InvariantCulture);
        var groups = variant == FallbackOverflow ? 3 : 1;
        var rootConstants = variant == FallbackRoot ? 16 : 60;
        var job = new MeshFallbackJob(
            _fixture.Workbench.Compile(variant + "_cs", compute, "cs"),
            _fixture.Workbench.Compile(variant + "_vs", vertex, "vs"),
            pixel,
            framebuffer,
            bindings with { RootConstantCount = rootConstants },
            groups,
            MaxOutputs * stride,
            MaxOutputs * 12)
        {
            RootDescriptors = variant == FallbackRoot,
        };

        if (variant == FallbackOverflow)
        {
            // Buffers for two groups (+4 spare index slots holding stale data whose first triangle, drawn, would repaint
            // pixels (4..7, 0)): the third group must be dropped without drawing anything.
            var stale = new uint[100];
            (stale[96], stale[97], stale[98]) = (0, 1, 3);
            job = job with { VertexBytes = 2 * Vertices * stride, IndexBytes = 100 * 4, StaleIndices = stale };
        }

        var result = _fixture.Runner.MeshFallback(job);
        var mismatches = ImageCheck.Compare(result, Size, Size, (x, y, _) => image[y, x]);
        uint[] arguments = [(uint)(groups * 3 * Primitives), 1, 0, 0, 0, (uint)(groups * Vertices)];

        if (!arguments.SequenceEqual(result.IndirectArguments ?? []))
        {
            mismatches.Add($"DrawIndexed arguments + vertex counter: expected {string.Join(", ", arguments)}, actual {string.Join(", ", result.IndirectArguments ?? [])}");
        }

        return mismatches;
    }

    /// <summary>The translated program (HLSL) of a variant.</summary>
    public string Translate(string variant)
    {
        var source = Program;

        if (variant == Windows)
        {
            source = Program.Replace(RoundTrip, string.Empty, StringComparison.Ordinal)
                .Replace(VertexRegion, VertexRegion + "\ns_mov_b32 s40, 7", StringComparison.Ordinal)
                .Replace(PrimitiveRegion, PrimitiveRegion + "\nv_mov_b32 v32, s9\nv_readfirstlane_b32 s9, v32", StringComparison.Ordinal);
        }

        var assembled = _fixture.Workbench.Assemble(variant, source, 64);
        var config = new ShaderConfig { Stage = HardwareStage.Ngg, WaveSize = 64, UserSgprs = 4, Ngg = new NggConfig(8, 0, 0), LdsBytes = 4 };
        var pipeline = new PipelineStateInfo { Stage = "ms", NumThreads = new ThreadGroupSize(64, 1, 1), MaxVertices = MaxOutputs, MaxPrimitives = MaxOutputs };
        var program = new ShaderProgram(ShaderStage.Mesh, assembled.Instructions, assembled.Code, ShaderSignatures.None, config, pipeline);

        if (variant is Supersteps or SuperstepFallback)
        {
            return HlslTranslator.SuperstepVariant(program, new TranslationOptions(StructuredControlFlow: false, PinWaveSize: false, EmulateWaveSize: true));
        }

        return ShaderWorkbench.Translate(program);
    }

    /// <summary>
    /// Vertex j at x = (j odd ? 8 : 0), y = j >> 1 (pixels) with param0 = (j, j >> 1, 100 + j, 0.5); primitive i of strip
    /// k = i >> 1 is (2k, 2k+1, 2k+2) when even, (2k+1, 2k+3, 2k+2) when odd; the culled one draws nothing.
    /// </summary>
    private static Expected[]?[,] Reference()
    {
        var image = new Expected[]?[Size, Size];

        for (var primitive = 0; primitive < Primitives; primitive++)
        {
            var strip = primitive >> 1;
            int[] indices = primitive % 2 == 0 ? [2 * strip, (2 * strip) + 1, (2 * strip) + 2] : [(2 * strip) + 1, (2 * strip) + 3, (2 * strip) + 2];

            if (primitive == Culled || indices.Max() >= Vertices)
            {
                continue;
            }

            var corners = indices.Select(j => ((double)((j & 1) * Size), (double)(j >> 1))).ToArray();
            var provoking = indices[0];
            Expected[] attribute = [FloatBits.Of(provoking), FloatBits.Of(provoking >> 1), FloatBits.Of(AttributeBase + provoking), FloatBits.Of(0.5)];
            Raster.Fill(image, corners, attribute);
        }

        return image;
    }

    [GeneratedRegex(@"vertex record stride (\d+) B")]
    private static partial Regex StridePattern();
}
