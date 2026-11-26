// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Loading;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// The names <c>--cpu-sample</c> reports samples under. Game code: "fn 0xRVA" (the .pdata function start in the exe, to
/// look up in the IDA database; another mapped image's functions carry its name). System DLLs: "module!Export".
/// Our code: the game's call into it, an import as "module!symbol" when the call goes through the IAT (the call-trace
/// thunk or the bound address named), else "[reg+0xNN] from fn 0xRVA" (a COM method: the vtable offset), with the
/// loader wait it is in and the DLL our code called. Blocked: the wait call, then where it waits.
/// </summary>
internal sealed unsafe class CpuSampleNames
{
    /// <summary>The bytes before a return address: FF 15 disp32 (call [rip+d]), FF 90-97 disp32, FF 50-57 disp8.</summary>
    private const int IndirectCall = 6;
    private const int ShortCall = 3;
    private const int ModRm = 5;
    private const int ShortModRm = 2;
    private const int Displacement = 4;
    private const byte Group5 = 0xFF;
    private const byte RipRelative = 0x15;
    private const byte ModMask = 0xF8;
    private const byte Displacement8 = 0x50;
    private const byte Displacement32 = 0x90;

    private readonly ImageTable _images;
    private readonly CallTrace _trace;
    private readonly (CodeRange Range, string Name)[] _stubs;
    private readonly ExportNames _exports = new();
    private readonly Dictionary<CpuSample, string> _labels = [];
    private Dictionary<nint, string>? _imports;

    public CpuSampleNames(ImageTable images, CallTrace trace, (CodeRange Range, string Name)[] stubs)
    {
        _images = images;
        _trace = trace;
        _stubs = stubs;
    }

    public string Label(CpuSample sample)
    {
        if (_labels.TryGetValue(sample, out var label))
        {
            return label;
        }

        label = sample.Bucket switch
        {
            CpuBucket.Game => Function(sample.Function),
            CpuBucket.Ours => Ours(sample),
            CpuBucket.System => _exports.Describe(sample.Pc),
            CpuBucket.Blocked => Blocked(sample),
            _ => "not read",
        };
        _labels[sample] = label;

        return label;
    }

    /// <summary>"fn 0xRVA" in the exe, "image fn 0xRVA" in another mapped image, else the system name.</summary>
    private string Function(ulong address)
    {
        var image = _images.FindByAddress((nint)address);

        if (image is null)
        {
            return _exports.Describe(address);
        }

        ulong rva = address - (ulong)image.Base;

        if (image.Pe.IsDll)
        {
            return $"{image.Name} fn 0x{rva:x}";
        }

        return $"fn 0x{rva:x}";
    }

    private string Blocked(CpuSample sample)
    {
        string stub = Stub(sample.Pc);

        if (sample.Ours)
        {
            return $"{stub} in ours: {Ours(sample with { Pc = 0 })}";
        }

        if (sample.Function != 0)
        {
            return $"{stub} from {Function(sample.Function)}";
        }

        return stub;
    }

    private string Ours(CpuSample sample)
    {
        string text = Site(sample.Site);

        if (sample.Wait is not null)
        {
            text = $"{text}: {sample.Wait}";
        }

        if (sample.Pc != 0)
        {
            text = $"{text} -> {_exports.Describe(sample.Pc)}";
        }

        return text;
    }

    private string Stub(ulong pc)
    {
        foreach (var (range, name) in _stubs)
        {
            if (range.Contains(pc))
            {
                return name;
            }
        }

        return _exports.Describe(pc);
    }

    /// <summary>The game's call at return address <paramref name="site"/> (its bytes were checked readable when found).</summary>
    private string Site(ulong site)
    {
        if (site == 0)
        {
            return "no game caller";
        }

        byte* code = (byte*)site;
        var image = _images.FindByAddress((nint)site);

        if (image is null || !ReturnAddresses.InCode(image, (nint)site) || !ReturnAddresses.CallBytesReadable((nint)site))
        {
            return $"call from {Function(Caller(site))}";
        }

        if (code[-IndirectCall] == Group5 && code[-ModRm] == RipRelative)
        {
            nint slot = (nint)site + *(int*)(code - Displacement);

            if (image.Contains(slot) && image.Contains(slot + sizeof(ulong) - 1))
            {
                return Target(*(nint*)slot);
            }
        }

        string from = Function(Caller(site));

        if (code[-ShortCall] == Group5 && (code[-ShortModRm] & ModMask) == Displacement8)
        {
            return $"[reg+0x{code[-1]:x}] from {from}";
        }

        if (code[-IndirectCall] == Group5 && (code[-ModRm] & ModMask) == Displacement32)
        {
            return $"[reg+0x{*(int*)(code - Displacement):x}] from {from}";
        }

        return $"call from {from}";
    }

    /// <summary>An import's name from the address its IAT slot holds: a call-trace thunk, a bound shim or DLL export.</summary>
    private string Target(nint target)
    {
        string? name = _trace.NameOf(target);

        if (name is not null)
        {
            return name;
        }

        _imports ??= Imports();

        if (_imports.TryGetValue(target, out name))
        {
            return name;
        }

        return _exports.Describe((ulong)target);
    }

    private Dictionary<nint, string> Imports()
    {
        var imports = new Dictionary<nint, string>();

        foreach (var record in _images.All.SelectMany(image => image.Imports))
        {
            imports.TryAdd(record.Address, $"{record.Module}!{record.Symbol}");
        }

        return imports;
    }

    /// <summary>The start of the function holding the call that returns to <paramref name="site"/>.</summary>
    private static ulong Caller(ulong site)
    {
        ulong imageBase = 0;
        nint function = Ntdll.RtlLookupFunctionEntry(site - 1, &imageBase, 0);

        if (function == 0)
        {
            return site;
        }

        return imageBase + *(uint*)function;
    }
}
