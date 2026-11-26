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
}
