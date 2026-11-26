// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Graphics.Replay;

/// <summary>
/// Writes a capture folder piece by piece: each resource's blobs as soon as its bytes are at hand (the loader maps its
/// readback buffers one at a time), the views, then the manifest last (<see cref="Finish"/>).
/// </summary>
public sealed class CaptureWriter
{
    private const string InputSuffix = ".in";
    private const string OutputSuffix = ".out";

    private readonly CaptureFolder _folder;
    private readonly List<CapturedResource> _resources = [];
    private readonly List<CapturedView> _views = [];
    private readonly List<string> _notes = [];

    public CaptureWriter(CaptureFolder folder)
    {
        _folder = folder;
    }

    public CaptureFolder Folder => _folder;

    /// <summary>
    /// Adds <paramref name="resource"/> with its bytes before the dispatch (empty: zeros) and after it (empty: not an
    /// output); the blobs are written now.
    /// </summary>
    public void Add(CapturedResource resource, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
    {
        string? input = null;
        string? output = null;

        if (!before.IsEmpty)
        {
            input = _folder.WriteBlob(resource.Id + InputSuffix, before);
        }

        if (!after.IsEmpty)
        {
            output = _folder.WriteBlob(resource.Id + OutputSuffix, after);
        }

        _resources.Add(resource with { Input = input, Output = output });
    }

    /// <summary>A blob of its own (a patch's bytes); its path for the manifest.</summary>
    public string Blob(string name, ReadOnlySpan<byte> bytes)
    {
        return _folder.WriteBlob(name, bytes);
    }

    /// <summary>A descriptor at <paramref name="index"/> of the view heap: a view of resource <paramref name="resource"/> (null: a null view).</summary>
    public void View(uint index, CapturedViewKind kind, string? resource, ReadOnlySpan<byte> description)
    {
        if (description.Length != CapturedView.DescriptionBytes && !description.IsEmpty)
        {
            throw new ArgumentException($"a view desc is {CapturedView.DescriptionBytes} bytes, not {description.Length}", nameof(description));
        }

        _views.RemoveAll(view => view.Index == index);
        _views.Add(new CapturedView(index, kind, resource, Convert.ToHexString(description)));
    }

    /// <summary>Something the capture could not keep, for the reader of the manifest.</summary>
    public void Note(string text)
    {
        _notes.Add(text);
    }

    /// <summary>Writes the manifest: the heap spans every view written.</summary>
    public DispatchCapture Finish(string pipeline, CapturedProgram program, bool userConstants, IReadOnlyList<uint> userData, IReadOnlyList<CapturedDispatch> dispatches, uint fallbackTable)
    {
        // The fallback table spans its three UAVs and one SRV (BindlessRootSignature).
        uint table = fallbackTable + Pipelines.BindlessRootSignature.FallbackUavs + 1;
        uint heap = Math.Max(table, _views.Select(view => view.Index + 1).DefaultIfEmpty(0u).Max());
        var views = _views.OrderBy(view => view.Index).ToList();
        var capture = new DispatchCapture(pipeline, program, userConstants, [.. userData], [.. dispatches], heap, fallbackTable, [.. _resources], views, [.. _notes]);
        _folder.Write(capture);

        return capture;
    }
}
