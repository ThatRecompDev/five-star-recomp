// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using FiveStar.Graphics.Pipelines;
using FiveStar.Graphics.Replay;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Shaders.Translation;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// Writes a finished capture (<see cref="DispatchCaptures"/>) as a folder <c>fivestar shader replay</c> runs: each source
/// from the readback buffer; the descriptor window as the program saw it (null records, sampler records naming their
/// index, the bytes of the tables it reads); an address map that names a view pair over each game memory range at its
/// console address; the reserved descriptors (the window, the map, the null views) and the program's files
/// (<see cref="CaptureProgram"/>).
/// </summary>
internal static class CaptureOutput
{
    private const string WindowId = "window";
    private const string MapId = "address-map";
    private const int SpanShift = 32;
    private const uint DwordBytes = sizeof(uint);
    private const uint ArgumentY = 1;
    private const uint ArgumentZ = 2;

    /// <summary>Writes the capture into <paramref name="folder"/> from the mapped readback buffer.</summary>
    public static DispatchCapture Write(CapturePlan plan, string folder, ReadOnlySpan<byte> readback)
    {
        var writer = new CaptureWriter(new CaptureFolder(folder));

        foreach (var source in plan.Sources)
        {
            var before = readback.Slice((int)source.Before, (int)source.Shape.Bytes);
            var after = source.Output ? readback.Slice((int)source.After, (int)source.Shape.Bytes) : [];
            writer.Add(source.Shape, before, after);
        }

        Window(writer, plan);
        Map(writer, plan);
        ReservedViews(writer, plan);

        foreach (var view in plan.Views)
        {
            writer.View(view.Index, view.Kind, view.Resource, view.Description);
        }

        foreach (var (source, pair) in plan.Mapped)
        {
            uint dwords = (uint)(source.Shape.Bytes / DwordBytes);
            writer.View(pair, CapturedViewKind.ShaderResource, source.Shape.Id, CaptureViews.Bytes(BufferViewDescription.RawShaderResource(dwords)));
            writer.View(pair + 1, CapturedViewKind.UnorderedAccess, source.Shape.Id, CaptureViews.Bytes(BufferViewDescription.RawUnorderedAccess(dwords)));
        }

        foreach (var note in plan.Notes)
        {
            writer.Note(note);
        }

        var program = CaptureProgram.Write(writer.Folder, plan.Pipeline);

        return writer.Finish(plan.Pipeline, program, plan.UserConstants, plan.UserData, Dispatches(plan, readback), DescriptorWindow.FallbackTableSlot);
    }

    /// <summary>The window: null view records, sampler records naming their index, then the bytes of the tables read.</summary>
    private static void Window(CaptureWriter writer, CapturePlan plan)
    {
        uint views = (DescriptorWindow.SamplerBase - DescriptorWindow.ViewBase) / DescriptorWindow.ViewRecordBytes;
        CapturedFill[] fills =
        [
            new(DescriptorWindow.ViewBase, DescriptorWindow.ViewRecordBytes, views, DescriptorWindow.NullViewSlot),
            new(DescriptorWindow.SamplerBase, DescriptorWindow.SamplerRecordBytes, DescriptorWindow.MaxSamplers, 0) { Step = 1 },
        ];
        var patches = plan.Patches.Select(patch => new CapturedPatch(patch.Offset, writer.Blob($"window-{patch.Offset:x}", patch.Bytes))).ToList();
        var shape = new CapturedResource(WindowId, "descriptor window", CapturedResource.BufferDimension, plan.WindowBytes, 1, 1, 1, 0, 0, plan.WindowBytes, null, null)
        {
            Fills = fills,
            Patches = patches,
        };
        writer.Add(shape, [], []);
    }

    /// <summary>An address map with a page table per 4 GiB span the ranges touch, each range mapped to its view pair.</summary>
    private static void Map(CaptureWriter writer, CapturePlan plan)
    {
        var map = new AddressMap(SpanCount(plan), DescriptorWindow.UnmappedSlot);
        var bytes = new byte[map.Bytes];
        map.Clear(bytes);

        foreach (var (source, pair) in plan.Mapped)
        {
            ulong address = source.Shape.ConsoleAddress!.Value;
            map.Map(bytes, address, source.Shape.Bytes, pair, address);
        }

        var shape = new CapturedResource(MapId, "address map", CapturedResource.BufferDimension, (ulong)bytes.Length, 1, 1, 1, 0, 0, (ulong)bytes.Length, null, null);
        writer.Add(shape, bytes, []);
    }

    private static IEnumerable<ulong> Spans(CapturedResource range)
    {
        ulong first = range.ConsoleAddress!.Value >> SpanShift;
        ulong last = (range.ConsoleAddress.Value + range.Bytes - 1) >> SpanShift;

        for (ulong span = first; span <= last; span++)
        {
            yield return span;
        }
    }

    /// <summary>The descriptors programs reach by constant (<see cref="DescriptorWindow"/>, rdna_prelude).</summary>
    private static void ReservedViews(CaptureWriter writer, CapturePlan plan)
    {
        uint window = plan.WindowBytes / DwordBytes;
        byte[] nullRead = CaptureViews.NullRaw();
        byte[] nullWrite = CaptureViews.Bytes(BufferViewDescription.RawUnorderedAccess(0));
        writer.View(DescriptorWindow.WindowSlot, CapturedViewKind.ShaderResource, WindowId, CaptureViews.Bytes(BufferViewDescription.RawShaderResource(window)));
        writer.View(DescriptorWindow.WritableWindowSlot, CapturedViewKind.UnorderedAccess, WindowId, CaptureViews.Bytes(BufferViewDescription.RawUnorderedAccess(window)));

        for (uint slot = 0; slot < BindlessRootSignature.FallbackUavs; slot++)
        {
            writer.View(DescriptorWindow.FallbackTableSlot + slot, CapturedViewKind.UnorderedAccess, null, nullWrite);
        }

        writer.View(DescriptorWindow.NullViewSlot, CapturedViewKind.ShaderResource, null, nullRead);
        writer.View(DescriptorWindow.UnmappedSlot, CapturedViewKind.ShaderResource, null, nullRead);
        writer.View(DescriptorWindow.UnmappedSlot + 1, CapturedViewKind.UnorderedAccess, null, nullWrite);
        var mapDwords = (uint)(new AddressMap(SpanCount(plan), DescriptorWindow.UnmappedSlot).Bytes / DwordBytes);
        writer.View(DescriptorWindow.AddressMapSlot, CapturedViewKind.ShaderResource, MapId, CaptureViews.Bytes(BufferViewDescription.RawShaderResource(mapDwords)));

        foreach (string type in NullTextureSlots.Views)
        {
            writer.View(NullTextureSlots.For(type), CapturedViewKind.ShaderResource, null, CaptureViews.Bytes(NullTextureViews.ShaderResource(type)));
        }

        foreach (string type in NullTextureSlots.Uavs)
        {
            writer.View(NullTextureSlots.For(type), CapturedViewKind.UnorderedAccess, null, CaptureViews.Bytes(NullTextureViews.UnorderedAccess(type)));
        }
    }

    /// <summary>The 4 GiB spans the game memory ranges touch: the map's page tables.</summary>
    private static int SpanCount(CapturePlan plan)
    {
        return plan.Mapped.SelectMany(entry => Spans(entry.Source.Shape)).Distinct().Count();
    }

    /// <summary>A Dispatch's group counts, or an ExecuteIndirect's records up to its count.</summary>
    private static List<CapturedDispatch> Dispatches(CapturePlan plan, ReadOnlySpan<byte> readback)
    {
        if (plan.Direct is not null)
        {
            return [plan.Direct];
        }

        var dispatches = new List<CapturedDispatch>();
        var arguments = plan.Indirect;

        if (arguments is null)
        {
            return dispatches;
        }

        uint count = arguments.Records;

        if (arguments.CountBuffer != 0)
        {
            count = Math.Min(count, BinaryPrimitives.ReadUInt32LittleEndian(readback[(int)(arguments.At + arguments.Bytes)..]));
        }

        for (uint record = 0; record < count; record++)
        {
            var at = readback[(int)(arguments.At + ((ulong)record * arguments.Stride))..];
            dispatches.Add(new CapturedDispatch(Dword(at, 0), Dword(at, ArgumentY), Dword(at, ArgumentZ)));
        }

        return dispatches;
    }

    private static uint Dword(ReadOnlySpan<byte> bytes, uint index)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes[(int)(index * DwordBytes)..]);
    }
}
