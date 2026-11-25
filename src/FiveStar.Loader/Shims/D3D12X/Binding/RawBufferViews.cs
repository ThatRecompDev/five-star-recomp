// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.Resources;

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// Raw views at the heap slots programs reach by index. A buffer record that keeps the desktop heap index
/// (<see cref="BufferRecords.Based"/>) or carries no V# fields sends a translated program to that slot, and every
/// translated program reads a buffer there as a ByteAddressBuffer (and writes it as a RWByteAddressBuffer at the same
/// index); a typed, structured or constant-buffer view in the slot is a type mismatch, undefined in D3D12, which may
/// fault or hang on NVIDIA. Such a view is written raw over the same bytes, or null (reads 0) when its first byte is off
/// D3D12's 16-byte raw alignment or it has no element size. Views whose record holds a console base are left as the
/// game made them: programs reach their bytes through the address map. <c>--typed-buffer-views</c> turns this off.
/// </summary>
internal static unsafe class RawBufferViews
{
    private static long _raw;
    private static long _nulled;
    private static long _readOnly;

    /// <summary>Whether kept-index buffer views are written raw (default on).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Views made raw or null, and read-only kept-index slots a program's write could reach (it writes at the same index).</summary>
    public static string Summary => $"kept-index buffer views: {Interlocked.Read(ref _raw)} made raw, {Interlocked.Read(ref _nulled)} null, {Interlocked.Read(ref _readOnly)} read-only slots a write could reach";

    /// <summary>
    /// The view to write for a buffer <paramref name="description"/> (SRV or, with <paramref name="unordered"/>, UAV) whose
    /// record is <paramref name="record"/>: as it is when its record holds a console base, it is raw already or this is
    /// off; else a raw copy in <paramref name="copy"/>, or a null raw view with <paramref name="resource"/> set to 0.
    /// </summary>
    [HotPath]
    public static void* View(void* description, byte* copy, bool unordered, BufferRecord record, ref nint resource)
    {
        var view = (BufferViewDescription*)description;

        if (view == null || view->Dimension != BufferViewDescription.BufferDimension || record.Base != 0)
        {
            return description;
        }

        if (!unordered)
        {
            Interlocked.Increment(ref _readOnly);
        }

        int flags = view->SrvFlags;

        if (unordered)
        {
            flags = view->UavFlags;
        }

        if (!Enabled || (flags & BufferViewDescription.RawFlag) != 0)
        {
            return description;
        }

        if (resource != 0 && TryRaw(view, copy, unordered))
        {
            Interlocked.Increment(ref _raw);

            return copy;
        }

        Interlocked.Increment(ref _nulled);
        resource = 0;
        Null(copy, unordered);

        return copy;
    }

    /// <summary>The view as a raw one over the same bytes in <paramref name="copy"/>; false when it cannot be.</summary>
    private static bool TryRaw(BufferViewDescription* view, byte* copy, bool unordered)
    {
        uint element = BufferViewRanges.ElementBytes((byte*)view, unordered);
        ulong first = view->SrvFirstElement;
        ulong count = view->SrvElements;

        if (unordered)
        {
            (first, count) = (view->UavFirstElement, view->UavElements);
        }

        if (element == 0)
        {
            return false;
        }

        return BufferViewRanges.AsRaw((byte*)view, copy, first * element, count * element, unordered);
    }

    /// <summary>The null raw SRV written for a constant-buffer view in a game heap that gets no raw view over its bytes.</summary>
    [HotPath]
    public static BufferViewDescription NullConstantBuffer()
    {
        Interlocked.Increment(ref _nulled);

        return BufferViewDescription.RawShaderResource(0);
    }

    private static void Null(byte* copy, bool unordered)
    {
        var view = BufferViewDescription.RawShaderResource(0);

        if (unordered)
        {
            view = BufferViewDescription.RawUnorderedAccess(0);
        }

        *(BufferViewDescription*)copy = view;
    }
}
