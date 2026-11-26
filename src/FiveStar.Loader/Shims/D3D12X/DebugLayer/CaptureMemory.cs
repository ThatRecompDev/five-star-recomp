// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Graphics.Replay;
using FiveStar.Loader.Shims.D3D12X.Binding;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// The game memory a capture reaches through the address map (<see cref="CapturePlan.Memory"/>): the ranges in whole
/// 64 KiB pages, merged where they meet, each copied from the desktop buffers the map names for its pages
/// (<see cref="GameMemoryViews.Locate"/>), as the program reads them; a page with no view stays zeros.
/// </summary>
internal static class CaptureMemory
{
    /// <summary>The address map's page (rdna_prelude gpu_page, <see cref="FiveStar.Shaders.Translation.AddressMap"/>).</summary>
    public const ulong PageBytes = 1 << 16;

    private const ulong PageMask = PageBytes - 1;
    private const ulong BeforeAndAfter = 2;

    /// <summary>Descriptors of a range's view pair: a raw SRV, then its raw UAV.</summary>
    public const uint PairSize = 2;

    /// <summary>Makes a source of each merged range and gives it a view pair in the replay heap, from <paramref name="firstPair"/>.</summary>
    public static void Map(CapturePlan plan, GameMemoryViews views, uint firstPair)
    {
        uint pair = firstPair;

        foreach (var (start, end) in Merged(plan.Memory))
        {
            ulong bytes = end - start;

            if (plan.SourceBytes + (BeforeAndAfter * bytes) > CaptureViews.MaxSourceBytes)
            {
                plan.Notes.Add($"game memory 0x{start:x} + 0x{bytes:x} not kept: past the capture's cap");

                continue;
            }

            var pieces = Pieces(views, start, end, out ulong missing);

            if (missing != 0)
            {
                plan.Notes.Add($"game memory 0x{start:x} + 0x{bytes:x}: {missing} pages without a view in the address map, kept as zeros");
            }

            var shape = new CapturedResource($"memory-{plan.Mapped.Count}", $"game memory 0x{start:x} + 0x{bytes:x}", CapturedResource.BufferDimension, bytes, 1, 1, 1, 0, 0, bytes, null, null)
            {
                ConsoleAddress = start,
            };
            var source = new CaptureSource(shape, pieces, 0, 0) { Output = true };
            plan.Sources.Add(source);
            plan.SourceBytes += BeforeAndAfter * bytes;
            plan.Mapped.Add((source, pair));
            pair += PairSize;
        }
    }

    /// <summary>The ranges in whole pages, sorted, those that overlap or touch joined.</summary>
    private static List<(ulong Start, ulong End)> Merged(IEnumerable<(ulong Start, ulong Bytes)> ranges)
    {
        var merged = new List<(ulong Start, ulong End)>();

        foreach (var (start, end) in ranges.Select(range => (range.Start & ~PageMask, (range.Start + range.Bytes + PageMask) & ~PageMask)).OrderBy(range => range.Item1))
        {
            if (merged.Count > 0 && start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));

                continue;
            }

            merged.Add((start, end));
        }

        return merged;
    }

    /// <summary>The copies of [<paramref name="start"/>, <paramref name="end"/>), pages next to each other in one buffer joined.</summary>
    private static List<CapturePiece> Pieces(GameMemoryViews views, ulong start, ulong end, out ulong missing)
    {
        var pieces = new List<CapturePiece>();
        missing = 0;

        for (ulong page = start; page < end; page += PageBytes)
        {
            if (!views.Locate(page, out nint buffer, out ulong offset))
            {
                missing++;

                continue;
            }

            if (pieces.Count > 0)
            {
                var last = pieces[^1];

                if (last.Buffer == buffer && last.SourceOffset + last.Bytes == offset && last.TargetOffset + last.Bytes == page - start)
                {
                    pieces[^1] = last with { Bytes = last.Bytes + PageBytes };

                    continue;
                }
            }

            pieces.Add(new CapturePiece(buffer, offset, page - start, PageBytes));
        }

        return pieces;
    }
}
