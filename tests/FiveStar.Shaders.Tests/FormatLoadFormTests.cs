// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using FiveStar.Shaders.Translation.Stages;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>A program with more formatted loads than ProgramEmitter.FormatLoadLimit gets the decode-only FmtLoad.</summary>
public sealed class FormatLoadFormTests
{
    [Theory]
    [InlineData(ProgramEmitter.FormatLoadLimit, false)]
    [InlineData(ProgramEmitter.FormatLoadLimit + 1, true)]
    public void Past_the_limit_the_prelude_decodes_every_formatted_load(int loads, bool decodeOnly)
    {
        var text = new StringBuilder("#include \"rdna_prelude.hlsli\"\nvoid main() {\n");

        for (int i = 0; i < loads; i++)
        {
            text.Append("  uint4 _f").Append(i).Append(" = FmtLoad(1u, 0u, 39u, 0xfacu);\n");
        }

        var result = ProgramEmitter.FormatLoadForm(text.Append("}\n").ToString());

        Assert.Equal(decodeOnly, result.StartsWith("#define FIVESTAR_FMT_DECODE_ONLY 1\n#include", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ProgramEmitter.SharedScalarLoadLimit / 4, false)]
    [InlineData((ProgramEmitter.SharedScalarLoadLimit / 4) + 1, true)]
    public void Past_the_limit_each_s_load_dword_walks_the_map_on_its_own(int loads, bool own)
    {
        var text = new StringBuilder("void main() {\n");

        for (int i = 0; i < loads; i++)
        {
            text.Append("  uint4 _t").Append(i).Append(" = sptr_views(s8, s9, 0x").Append(i * 16).Append("u);\n");

            for (int dword = 0; dword < 4; dword++)
            {
                text.Append("  s").Append(dword).Append(" = SPtrLoadAt(_t").Append(i).Append(", ").Append(dword * 4).Append("u, s8, s9, 0x").Append(i * 16).Append("u + ").Append(dword).Append("u * 4u);\n");
            }
        }

        var result = ProgramEmitter.ScalarLoadForm(text.Append("}\n").ToString());

        Assert.Equal(own, !result.Contains("sptr_views", StringComparison.Ordinal));
        Assert.Equal(own, result.Contains("  s3 = SPtrLoad(s8, s9, 0x0u + 3u * 4u);\n", StringComparison.Ordinal));
    }
}
