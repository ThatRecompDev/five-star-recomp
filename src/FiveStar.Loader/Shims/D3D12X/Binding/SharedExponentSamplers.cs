// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using FiveStar.Shaders.Disassembly;
using FiveStar.Shaders.Hardware;
using FiveStar.Shaders.Translation;

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// <c>--typed-image-views</c>: the programs seen sampling shared-exponent light (a class-3 record, an R9G9B9E5 view of an
/// R32_UINT texture), which filter it themselves only when listed (TranslationOptions.SharedExponentPrograms). Each
/// pipeline keeps the records its programs sample through (<see cref="TableReach.Sampled"/>, from their FSSM files); once
/// the game has made a class-3 view, a draw or dispatch reads those records' class in the window, and a program that
/// samples one is added to <c>shared-exponent-programs.txt</c> in the program cache, which the next boot lists.
/// </summary>
internal static unsafe class SharedExponentSamplers
{
    /// <summary>The list's file in the program cache: one program name per line.</summary>
    public const string FileName = "shared-exponent-programs.txt";

    private const string ProgramExtension = ".fssm";
    private const string Separator = " + ";
    private const int Wave64 = 64;
    private const uint ClassMask = 3;
    private const int ClassDword = 3;

    private static readonly ConcurrentDictionary<string, IReadOnlyList<(int Sgpr, uint Offset)>> Programs = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<nint, SampledRecord[]> Pipelines = new();
    private static readonly ConcurrentDictionary<string, byte> Listed = new(StringComparer.Ordinal);
    private static readonly object FileGate = new();
    private static volatile bool _seen;

    /// <summary>The program cache folder the list lives in; null records nothing.</summary>
    public static string? Folder { get; set; }

    /// <summary>The programs listed by earlier boots, read from <see cref="Folder"/> (none when it has no list).</summary>
    public static IReadOnlyList<string> Load(string? folder)
    {
        Folder = folder;
        var path = folder is null ? null : Path.Combine(folder, FileName);

        if (path is null || !File.Exists(path))
        {
            return [];
        }

        var names = File.ReadAllLines(path).Select(line => line.Trim()).Where(line => line.Length > 0).ToList();

        foreach (var name in names)
        {
            Listed.TryAdd(name, 0);
        }

        return names;
    }

    /// <summary>The game made a class-3 view: draws from now on check what they sample.</summary>
    public static void NoteView()
    {
        _seen = true;
    }

    /// <summary>Keeps the sampled records of <paramref name="pipeline"/>'s programs <paramref name="names"/> (" + "-joined) not yet listed.</summary>
    public static void Register(nint pipeline, string names)
    {
        if (pipeline == 0 || Folder is null || TableReaches.ProgramFolder is null || !ImageViewClasses.Enabled)
        {
            return;
        }

        var records = new List<SampledRecord>();

        foreach (var name in names.Split(Separator, StringSplitOptions.RemoveEmptyEntries))
        {
            var source = TableReaches.Source(name);

            if (Listed.ContainsKey(source))
            {
                continue;
            }

            records.AddRange(Program(source).Select(record => new SampledRecord(source, record.Sgpr, record.Offset)));
        }

        Keep(pipeline, [.. records]);
    }

    /// <summary>Keeps <paramref name="records"/> as the records <paramref name="pipeline"/>'s draws check.</summary>
    public static void Keep(nint pipeline, SampledRecord[] records)
    {
        Pipelines[pipeline] = records;
    }

    /// <summary>
    /// At a draw or dispatch, once a class-3 view exists: lists each program of <paramref name="pipeline"/> that samples
    /// a class-3 record through the tables in <paramref name="userData"/>.
    /// </summary>
    [HotPath]
    public static void Check(nint pipeline, uint* userData, byte* window)
    {
        if (!_seen || window == null || !Pipelines.TryGetValue(pipeline, out var records) || records.Length == 0)
        {
            return;
        }

        foreach (var record in records)
        {
            if (record.Sgpr >= CommandListBindings.UserDataDwords)
            {
                continue;
            }

            ulong at = (ulong)userData[record.Sgpr] + record.Offset;

            if (at + DescriptorWindow.ViewRecordBytes <= DescriptorWindow.RecordsEnd && (((uint*)(window + at))[ClassDword] & ClassMask) == ImageViewClasses.SharedExponent)
            {
                List(pipeline, record);
            }
        }
    }

    /// <summary>Adds the record's program to the list (once) and drops it from the pipeline's checks.</summary>
    private static void List(nint pipeline, SampledRecord record)
    {
        if (Listed.TryAdd(record.Program, 0))
        {
            lock (FileGate)
            {
                File.AppendAllLines(Path.Combine(Folder!, FileName), [record.Program]);
            }

            ShimContext.Log?.Warn($"[d3d12_xs] {record.Program} samples shared-exponent light (user SGPR {record.Sgpr}, record +0x{record.Offset:x}): listed in {FileName}, it filters it from the next boot (--shared-exponent-programs)");
        }

        if (Pipelines.TryGetValue(pipeline, out var records))
        {
            Pipelines[pipeline] = [.. records.Where(entry => entry.Program != record.Program)];
        }
    }

    /// <summary>A program's sampled records, from its FSSM file (none when it is missing or does not decode).</summary>
    private static IReadOnlyList<(int Sgpr, uint Offset)> Program(string source)
    {
        return Programs.GetOrAdd(source, name =>
        {
            var path = Path.Combine(TableReaches.ProgramFolder!, name + ProgramExtension);

            if (!File.Exists(path))
            {
                return [];
            }

            try
            {
                var bytes = File.ReadAllBytes(path);
                var config = FssmHeader.Decode(bytes);

                return TableReach.Sampled(new RdnaDecoder().Disassemble(FssmHeader.ProgramCode(bytes), config.WaveSize ?? Wave64));
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or NotSupportedException)
            {
                return [];
            }
        });
    }
}
