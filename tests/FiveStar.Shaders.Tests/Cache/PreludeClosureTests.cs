// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Cache;
using FiveStar.Shaders.Translation;
using Xunit;

namespace FiveStar.Shaders.Tests.Cache;

/// <summary>
/// The program content store's key holds what a program's compile reads from the runtime headers: the functions it
/// reaches and everything that is not a function. A change to a function it does not reach leaves its key; any other
/// header change moves it.
/// </summary>
public sealed class PreludeClosureTests
{
    private const string Header = "test.hlsli";

    private const string Base = """
        #define LIMIT 4u
        static const uint TABLE[2] = { 1u, 2u };
        uint helper(uint x) { return x + TABLE[1]; }
        uint used(uint x) {
            return helper(x) * LIMIT;
        }
        uint unused(uint x) { return x - 1u; }
        #ifdef FEATURE
        uint conditional(uint x) { return used(x); }
        #endif
        """;

    private const string Program = "#include \"test.hlsli\"\nvoid main() { uint v = used(3u); }\n";

    [Fact]
    public void A_change_to_a_function_the_program_does_not_reach_leaves_its_key()
    {
        Assert.Equal(Key(Base), Key(Base.Replace("x - 1u", "x - 2u", StringComparison.Ordinal)));
        Assert.Equal(Key(Base), Key(Base + "\nuint added(uint x) { return x; }\n"));
        Assert.Equal(Key(Base), Key(Base.Replace("{ return used(x); }", "{ return used(x) + 1u; }", StringComparison.Ordinal)));
        Assert.Equal(Key(Base), Key("// a note on the table\n" + Base));
    }

    [Theory]
    [InlineData("return helper(x) * LIMIT;", "return helper(x) * LIMIT + 1u;")]
    [InlineData("return x + TABLE[1];", "return x + TABLE[0];")]
    [InlineData("{ 1u, 2u }", "{ 1u, 3u }")]
    [InlineData("#define LIMIT 4u", "#define LIMIT 5u")]
    public void A_change_to_what_the_program_reaches_or_to_anything_not_a_function_moves_its_key(string before, string after)
    {
        Assert.NotEqual(Key(Base), Key(Base.Replace(before, after, StringComparison.Ordinal)));
    }

    [Fact]
    public void A_new_overload_of_a_name_the_program_reaches_moves_its_key()
    {
        Assert.NotEqual(Key(Base), Key(Base + "\nuint used(float x) { return 0u; }\n"));
    }

    [Fact]
    public void The_runtime_prelude_splits_into_its_functions_and_the_rest()
    {
        var prelude = PreludeClosure.Header.Parse("rdna_prelude.hlsli", RuntimeIncludes.Read("rdna_prelude.hlsli"));

        Assert.Contains("FmtLoad", prelude.Functions.Keys);
        Assert.Contains("SPtrLoad", prelude.Functions.Keys);
        Assert.Contains("gpu_page", prelude.Functions.Keys);

        // Pieces cover the text: nothing is lost between them.
        Assert.Equal(Normalize(prelude.Text), Normalize(string.Concat(prelude.Pieces.Select(Joined))));
    }

    /// <summary>A piece as it stands in its header: a function piece lacks the line break after its closing brace.</summary>
    private static string Joined(PreludeClosure.Piece piece)
    {
        if (piece.Name is null)
        {
            return piece.Text;
        }

        return piece.Text + "\n";
    }

    /// <summary>
    /// The edit of the DXIL check (docs/d3d12x-pipelines.md): an unused function near the top, which moves every later
    /// line, and a changed formatted load. On 52 sampled programs the 28 that read no formatted load compiled to the same
    /// bytes with the DXIL backend and with dxc; the 24 that do did not.
    /// </summary>
    [Fact]
    public void A_program_that_reads_no_formatted_load_keeps_its_key_when_the_formatted_load_changes()
    {
        var prelude = RuntimeIncludes.Read("rdna_prelude.hlsli");
        var edited = prelude
            .Replace("uint sampler_slot(uint i) { return min(i, SAMPLER_SLOTS - 1u); }\n", "uint sampler_slot(uint i) { return min(i, SAMPLER_SLOTS - 1u); }\nuint unused_helper(uint a, uint b) {\n    return (a ^ b) * 3u;\n}\n", StringComparison.Ordinal)
            .Replace("    uint base = addr & ~3u, sh = (addr & 3u) * 8u;\n#ifndef FIVESTAR_FMT_DECODE_ONLY", "    uint base = (addr + 4u) & ~3u, sh = (addr & 3u) * 8u;\n#ifndef FIVESTAR_FMT_DECODE_ONLY", StringComparison.Ordinal);
        const string Scalar = "#include \"rdna_prelude.hlsli\"\n[numthreads(64, 1, 1)] void main() { uint v = SPtrLoad(0u, 0u, 4u); }\n";
        const string Formatted = "#include \"rdna_prelude.hlsli\"\n[numthreads(64, 1, 1)] void main() { uint4 v = FmtLoad(1u, 0u, 39u, 0xfacu); }\n";

        Assert.Contains("unused_helper", edited, StringComparison.Ordinal);
        Assert.Contains("(addr + 4u)", edited, StringComparison.Ordinal);
        Assert.Equal(KeyOf(Scalar, prelude), KeyOf(Scalar, edited));
        Assert.NotEqual(KeyOf(Formatted, prelude), KeyOf(Formatted, edited));
    }

    private static string Key(string header)
    {
        return PreludeClosure.Of(Program, name => Only(Header, header, name));
    }

    private static string KeyOf(string program, string prelude)
    {
        return PreludeClosure.Of(program, name => Only("rdna_prelude.hlsli", prelude, name));
    }

    /// <summary>The one header <paramref name="known"/> with <paramref name="text"/>; null for any other name.</summary>
    private static PreludeClosure.Header? Only(string known, string text, string name)
    {
        if (name != known)
        {
            return null;
        }

        return PreludeClosure.Header.Parse(name, text);
    }

    private static string Normalize(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
    }
}
