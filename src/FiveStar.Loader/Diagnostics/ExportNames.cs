// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Loading;
using FiveStar.Loader.Native;
using FiveStar.Loader.Pe;

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// Names code in OS-loaded modules without symbols: the function around an address (its .pdata entry) as
/// "module!Export" when an export starts there, else "module!Export+0xoffset" after the nearest export below it, so
/// samples of one function share a name. Each module's export table is read once.
/// </summary>
internal sealed unsafe class ExportNames
{
    private readonly Dictionary<nint, (string File, uint[] Rvas, string[] Names)> _modules = [];

    /// <summary>The name of the function around <paramref name="address"/>; "0x..." outside every OS-loaded module.</summary>
    public string Describe(ulong address)
    {
        nint module;
        uint flags = Win32Constants.GetModuleHandleExFlagFromAddress | Win32Constants.GetModuleHandleExFlagUnchangedRefcount;

        if (Kernel32.GetModuleHandleExW(flags, (char*)address, &module) == 0)
        {
            return $"0x{address:x}";
        }

        var (file, rvas, names) = Module(module);
        uint rva = FunctionRva(address, module);
        int index = Array.BinarySearch(rvas, rva);

        if (index >= 0)
        {
            return $"{file}!{names[index]}";
        }

        int below = ~index - 1;

        if (below < 0)
        {
            return $"{file}+0x{rva:x}";
        }

        return $"{file}!{names[below]}+0x{rva - rvas[below]:x}";
    }

    /// <summary>The RVA of the .pdata function around the address, else the address's own RVA.</summary>
    private static uint FunctionRva(ulong address, nint module)
    {
        ulong imageBase = 0;
        nint function = Ntdll.RtlLookupFunctionEntry(address, &imageBase, 0);

        if (function == 0 || imageBase != (ulong)module)
        {
            return (uint)(address - (ulong)module);
        }

        return *(uint*)function;
    }

    private (string File, uint[] Rvas, string[] Names) Module(nint module)
    {
        if (_modules.TryGetValue(module, out var known))
        {
            return known;
        }

        byte* headers = (byte*)module;
        int ntHeader = *(int*)(headers + PeFormat.NewHeaderPointerOffset);
        byte* directories = headers + ntHeader + PeFormat.NtSignatureSize + PeFormat.FileHeaderSize + PeFormat.DataDirectoriesOffset;
        var directory = new DataDirectory(*(uint*)directories, *(uint*)(directories + sizeof(uint)));
        var exports = new ExportTable(module, directory).Entries()
            .Where(entry => entry.Name is not null && entry.Forwarder is null)
            .GroupBy(entry => entry.Rva)
            .Select(group => (group.Key, Name: group.Select(entry => entry.Name!).Order(StringComparer.Ordinal).First()))
            .OrderBy(entry => entry.Key)
            .ToArray();

        known = (SystemModules.FileName(module), [.. exports.Select(entry => entry.Key)], [.. exports.Select(entry => entry.Name)]);
        _modules[module] = known;

        return known;
    }
}
