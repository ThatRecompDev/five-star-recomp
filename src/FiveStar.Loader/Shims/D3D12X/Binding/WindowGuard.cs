// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using FiveStar.Loader.Diagnostics;
using FiveStar.Shaders.Cache;
using FiveStar.Shaders.Translation;

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// The window guard (on unless <c>--no-window-guard</c>): a program's store or atomic through a 32-bit pointer (high
/// dword 0) would land in the descriptor window, whose records and debug rings programs have no business writing; the
/// prelude (FIVESTAR_WINDOW_GUARD) drops it and counts it in slot <c>id &amp; 1023</c> of a table past the room for view
/// records: {program id (<see cref="MeshProbe.Id"/> of its name, FIVESTAR_PROGRAM_ID), drops}; an atomic goes to the
/// sink dword after the table. At every captured present the programs that dropped stores since the last are logged.
/// Such stores come from pointers built from a V# base that holds a heap index, and from 32-bit table pointers.
/// </summary>
internal static unsafe class WindowGuard
{
    /// <summary>The define of guarded programs; its value is the table's window offset.</summary>
    public const string Define = "FIVESTAR_WINDOW_GUARD";

    /// <summary>The define of a program's id, which its drops are counted under.</summary>
    public const string ProgramDefine = "FIVESTAR_PROGRAM_ID";

    /// <summary>The table's window offset: right past the room for view records.</summary>
    public const uint Base = DescriptorWindow.RecordsEnd;

    /// <summary>The table and the sink, padded to 256 bytes.</summary>
    public const uint RegionBytes = 0x2100;

    /// <summary>Window bytes the guard needs.</summary>
    public const uint Bytes = Base + RegionBytes;

    private const uint Slots = 1024;

    /// <summary>A slot: the program id, then its drops.</summary>
    private const uint SlotDwords = 2;
    private const int Shown = 12;
    private const string Prefix = "  [window guard] ";

    private static readonly uint[] Reported = new uint[Slots];
    private static Dictionary<uint, string>? _names;

    /// <summary>Whether programs made on demand drop their stores into the window.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>The extracted programs, for the names of the ids in the table.</summary>
    public static ProgramIndex? Index { get; set; }

    /// <summary>The mode define (<see cref="Pipelines.ProgramProducer.ModeHeader"/>).</summary>
    public static string Header => $"#define {Define} {Base}u\n";

    /// <summary>The define of <paramref name="name"/>'s id, for a program made with the guard (its header holds <see cref="Define"/>).</summary>
    public static string ProgramHeader(string header, string name)
    {
        if (!header.Contains(Define, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return $"#define {ProgramDefine} {MeshProbe.Id(DebugLayer.MeshProbes.Source(name))}u\n";
    }

    /// <summary>Logs the programs whose stores into the window were dropped since the last report; the GPU is idle.</summary>
    public static void Report(long present, LoaderLog log)
    {
        byte* window = ShimContext.Runtime.D3D12X.Bindings.Window.Data;

        if (!Enabled || window == null)
        {
            return;
        }

        var dropped = new List<(uint Id, uint Count)>();
        uint* table = (uint*)(window + Base);

        for (uint slot = 0; slot < Slots; slot++)
        {
            uint count = table[(slot * SlotDwords) + 1];
            uint fresh = count - Reported[slot];
            Reported[slot] = count;

            if (fresh != 0)
            {
                dropped.Add((table[slot * SlotDwords], fresh));
            }
        }

        if (dropped.Count == 0)
        {
            log.Info($"{Prefix}present {present}: no program stored into the window");

            return;
        }

        var names = Names();
        var text = new StringBuilder($"{Prefix}present {present}: {dropped.Count} programs stored into the window (dropped):");

        foreach (var (id, count) in dropped.OrderByDescending(entry => entry.Count).Take(Shown))
        {
            text.Append($"\n{Prefix}  {count} by {names.GetValueOrDefault(id, $"program 0x{id:x8}")}");
        }

        log.Info(text.ToString());
    }

    private static Dictionary<uint, string> Names()
    {
        if (_names is null && Index is not null)
        {
            _names = [];

            foreach (var name in Index.Names)
            {
                _names[MeshProbe.Id(DebugLayer.MeshProbes.Source(name))] = DebugLayer.MeshProbes.Source(name);
            }
        }

        return _names ?? [];
    }
}
