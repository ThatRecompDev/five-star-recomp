// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// With <c>--aftermath</c>: the Aftermath hash of every shader of every desktop pipeline made (GFSDK_Aftermath_GetShaderHash
/// over its DXIL, the hash a crash dump's active shaders carry), so the dump's shaders are named by their pipelines'
/// translated programs (<see cref="QueueProgress.PipelineName"/>). Names are looked up at the crash, when every
/// pipeline has its name.
/// </summary>
internal sealed unsafe class AftermathShaderTable
{
    /// <summary>The pipelines named per shader in a report; a shared vertex shader belongs to thousands.</summary>
    private const int NamedPipelines = 4;

    private readonly ConcurrentDictionary<ulong, PipelineStateSubObjectType> _stages = new();
    private readonly ConcurrentDictionary<(ulong Hash, nint Pipeline), byte> _uses = new();
    private readonly delegate* unmanaged[Cdecl]<uint, ShaderBytecode*, ulong*, uint> _hash;
    private readonly uint _version;

    /// <summary>D3D12_SHADER_BYTECODE (d3d12.h).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ShaderBytecode
    {
        public void* Code;
        public nuint Length;
    }

    /// <summary>Over GFSDK_Aftermath_GetShaderHash(version, const D3D12_SHADER_BYTECODE*, GFSDK_Aftermath_ShaderBinaryHash*) at <paramref name="hashExport"/>; 0 notes nothing.</summary>
    public AftermathShaderTable(nint hashExport, uint version)
    {
        _hash = (delegate* unmanaged[Cdecl]<uint, ShaderBytecode*, ulong*, uint>)hashExport;
        _version = version;
    }

    /// <summary>Whether the DLL hashes shaders.</summary>
    public bool Enabled => _hash != null;

    /// <summary>Distinct shaders noted.</summary>
    public int Count => _stages.Count;

    /// <summary>A shader of <paramref name="pipeline"/> at stage <paramref name="stage"/>.</summary>
    public void Note(nint pipeline, PipelineStateSubObjectType stage, byte* code, int length)
    {
        if (!Enabled || pipeline == 0 || code == null)
        {
            return;
        }

        var bytecode = new ShaderBytecode { Code = code, Length = (nuint)length };
        ulong hash = 0;

        if (!AftermathLibrary.Succeeded(_hash(_version, &bytecode, &hash)))
        {
            return;
        }

        _stages.TryAdd(hash, stage);
        _uses.TryAdd((hash, pipeline), 0);
    }

    /// <summary>"pixel shader of 3 pipelines: name, ..." for a shader hash; null when no pipeline of ours had it.</summary>
    public string? Describe(ulong hash, Func<nint, string> pipelineName)
    {
        if (!_stages.TryGetValue(hash, out var stage))
        {
            return null;
        }

        var pipelines = Pipelines(hash);
        string names = string.Join("; ", pipelines.Take(NamedPipelines).Select(pipeline => $"0x{pipeline:x} {pipelineName(pipeline)}"));
        string more = string.Empty;

        if (pipelines.Count > NamedPipelines)
        {
            more = $"; and {pipelines.Count - NamedPipelines} more";
        }

        return $"{stage} of {pipelines.Count} pipelines: {names}{more}";
    }

    /// <summary>Writes every noted shader as "hash, stage, pipeline, name" lines (tab-separated) to <paramref name="path"/>.</summary>
    public void Write(string path, Func<nint, string> pipelineName)
    {
        var text = new StringBuilder("shader hash\tstage\tpipeline\tname\n");

        foreach (var (hash, pipeline) in _uses.Keys.OrderBy(use => use.Hash))
        {
            text.Append(CultureInfo.InvariantCulture, $"0x{hash:x16}\t{_stages.GetValueOrDefault(hash)}\t0x{pipeline:x}\t{pipelineName(pipeline)}\n");
        }

        File.WriteAllText(path, text.ToString());
    }

    private List<nint> Pipelines(ulong hash)
    {
        return [.. _uses.Keys.Where(use => use.Hash == hash).Select(use => use.Pipeline)];
    }
}
