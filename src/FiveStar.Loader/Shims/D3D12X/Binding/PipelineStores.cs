// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using FiveStar.Shaders.Disassembly;
using FiveStar.Shaders.Hardware;

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// Which pipelines' programs store to memory (buffer, global, flat, image or scalar stores and atomics), decoded once per
/// program from its FSSM file as <see cref="TableReaches"/> does: work of a pipeline that stores nothing cannot have
/// written an indirect argument buffer, so the records of a later ExecuteIndirect may be clamped before the list runs
/// (<see cref="IndirectGather"/>). A pipeline not registered, or one of whose programs did not decode, counts as storing.
/// </summary>
internal static class PipelineStores
{
    private const string ProgramExtension = ".fssm";
    private const string Separator = " + ";
    private const int Wave64 = 64;

    /// <summary>Mnemonic prefixes of instructions that write memory other programs or the command processor can read.</summary>
    private static readonly string[] StorePrefixes =
    [
        "buffer_store", "buffer_atomic", "tbuffer_store", "global_store", "global_atomic", "flat_store", "flat_atomic",
        "image_store", "image_atomic", "s_store", "s_buffer_store", "s_atomic", "s_buffer_atomic",
    ];

    private static readonly ConcurrentDictionary<string, bool> Programs = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<nint, bool> Pipelines = new();

    /// <summary>Whether pipelines are registered (<c>--indirect-gather</c>).</summary>
    public static bool Enabled { get; set; }

    /// <summary>Notes whether <paramref name="pipeline"/>, built from the programs <paramref name="names"/> (" + "-joined), stores to memory.</summary>
    public static void Register(nint pipeline, string names)
    {
        if (!Enabled || pipeline == 0 || TableReaches.ProgramFolder is null)
        {
            return;
        }

        bool stores = false;

        foreach (var name in names.Split(Separator, StringSplitOptions.RemoveEmptyEntries))
        {
            stores |= Program(name);
        }

        Note(pipeline, stores);
    }

    /// <summary>Notes whether <paramref name="pipeline"/> stores to memory.</summary>
    internal static void Note(nint pipeline, bool stores)
    {
        Pipelines[pipeline] = stores;
    }

    /// <summary>Whether work of <paramref name="pipeline"/> may write memory; true for one not registered.</summary>
    [HotPath]
    public static bool Stores(nint pipeline)
    {
        return !Pipelines.TryGetValue(pipeline, out bool stores) || stores;
    }

    /// <summary>Whether <paramref name="pipeline"/> was registered (its programs decoded or found missing), not merely unknown.</summary>
    [HotPath]
    public static bool Registered(nint pipeline)
    {
        return Pipelines.ContainsKey(pipeline);
    }

    /// <summary>Whether a program's instructions write memory (the mnemonics of <see cref="StorePrefixes"/>).</summary>
    public static bool Writes(IReadOnlyList<Instruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            string text = instruction.Text.TrimStart();

            foreach (var prefix in StorePrefixes)
            {
                if (text.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether the program stores to memory; true when its file is missing or does not decode.</summary>
    private static bool Program(string name)
    {
        return Programs.GetOrAdd(TableReaches.Source(name), source =>
        {
            var path = Path.Combine(TableReaches.ProgramFolder!, source + ProgramExtension);

            if (!File.Exists(path))
            {
                return true;
            }

            try
            {
                var bytes = File.ReadAllBytes(path);
                var config = FssmHeader.Decode(bytes);

                return Writes(new RdnaDecoder().Disassemble(FssmHeader.ProgramCode(bytes), config.WaveSize ?? Wave64));
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or NotSupportedException)
            {
                return true;
            }
        });
    }
}
