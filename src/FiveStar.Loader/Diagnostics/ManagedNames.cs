// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// Names the loader's managed code for <c>--cpu-sample</c>: each method of the loader's assembly by the start of the
/// code it runs now (its entry followed through the runtime's precode jumps, then its unwind data's function start).
/// A method not compiled yet, or recompiled since the table was made, is not named until the table is made again
/// (<see cref="Refresh"/>, at most once per report). Starts that several methods share (the runtime's own stubs) name
/// none. Inlined methods are part of their caller.
/// </summary>
internal sealed unsafe class ManagedNames
{
    /// <summary>The jumps of a precode: jmp [rip+d] (FF 25 d32), jmp rel32 (E9), mov r10, [rip+d] (4C 8B 15 d32) first.</summary>
    private const byte Group5 = 0xFF;
    private const byte RipRelativeJump = 0x25;
    private const byte JumpRelative = 0xE9;
    private const byte Rex = 0x4C;
    private const byte Move = 0x8B;
    private const byte R10RipRelative = 0x15;
    private const int IndirectJumpBytes = 6;
    private const int RelativeJumpBytes = 5;
    private const int MoveBytes = 7;
    private const int DisplacementAt = 2;
    private const int RelativeAt = 1;
    private const int MaxHops = 4;

    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    private readonly Assembly _assembly;
    private Dictionary<ulong, string> _names = [];

    public ManagedNames(Assembly assembly)
    {
        _assembly = assembly;
    }

    /// <summary>"Type.Method" for a function start, or null.</summary>
    public string? Find(ulong start)
    {
        return _names.GetValueOrDefault(start);
    }

    /// <summary>Makes the table again from the methods' current code.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "A diagnostic: a method trimmed away is not named.")]
    public void Refresh()
    {
        var names = new Dictionary<ulong, string>();
        var shared = new HashSet<ulong>();

        foreach (var type in Types())
        {
            foreach (var method in type.GetMethods(Declared))
            {
                ulong start = CodeStart(method);

                if (start == 0 || shared.Contains(start))
                {
                    continue;
                }

                if (!names.TryAdd(start, $"{type.Name}.{method.Name}"))
                {
                    names.Remove(start);
                    shared.Add(start);
                }
            }
        }

        _names = names;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "A diagnostic: a type trimmed away is not named.")]
    private IEnumerable<Type> Types()
    {
        try
        {
            return _assembly.GetTypes().Where(type => !type.ContainsGenericParameters);
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>().Where(type => !type.ContainsGenericParameters);
        }
    }

    /// <summary>The function start of the code <paramref name="method"/> runs now; 0 when it has none to name.</summary>
    private static ulong CodeStart(MethodInfo method)
    {
        if (method.IsAbstract || method.ContainsGenericParameters || (method.MethodImplementationFlags & MethodImplAttributes.InternalCall) != 0
            || (method.Attributes & MethodAttributes.PinvokeImpl) != 0)
        {
            return 0;
        }

        nint entry;

        try
        {
            entry = method.MethodHandle.GetFunctionPointer();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return 0;
        }

        ulong code = Follow((ulong)entry);
        ulong imageBase = 0;
        nint function = Ntdll.RtlLookupFunctionEntry(code, &imageBase, 0);

        if (function == 0)
        {
            return 0;
        }

        return imageBase + *(uint*)function;
    }

    /// <summary>Follows a precode's jumps to the code they lead to.</summary>
    private static ulong Follow(ulong address)
    {
        for (int hop = 0; hop < MaxHops && address != 0; hop++)
        {
            byte* code = (byte*)address;

            if (code[0] == Rex && code[1] == Move && code[DisplacementAt] == R10RipRelative)
            {
                code += MoveBytes;
            }

            if (code[0] == Group5 && code[1] == RipRelativeJump)
            {
                address = *(ulong*)(code + IndirectJumpBytes + *(int*)(code + DisplacementAt));
            }
            else if (code[0] == JumpRelative)
            {
                address = (ulong)(code + RelativeJumpBytes + *(int*)(code + RelativeAt));
            }
            else
            {
                break;
            }
        }

        return address;
    }
}
