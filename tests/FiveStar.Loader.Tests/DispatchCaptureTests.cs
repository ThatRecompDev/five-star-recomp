// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using FiveStar.Graphics;
using FiveStar.Graphics.Replay;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using FiveStar.Loader.Tests.Support;
using FiveStar.Shaders.Compilation;
using FiveStar.Shaders.Translation;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// `--capture-dispatch`'s writer: a capture of a program that reads a V# from its table in the descriptor window and
/// game memory at console addresses in two 4 GiB spans, through the address map and reserved views the writer makes,
/// replays on WARP to the bytes the capture holds.
/// </summary>
public sealed class DispatchCaptureTests : IDisposable
{
    private const uint Threads = 64;
    private const uint TableOffset = 0x2000;
    private const ulong InputAddress = 0x2_0001_0000;
    private const ulong OutputAddress = 0x5_0000_0000;

    private const string Program = """
        #include "rdna_prelude.hlsli"

        [numthreads(64, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            uint d0 = MemLoad(user_sgpr(4), 0u);
            uint d1 = MemLoad(user_sgpr(4), 4u);
            uint value = PtrLoad(d0, d1 & 0xffffu, id.x * 4u);
            PtrStore(user_sgpr(6), user_sgpr(7), id.x * 4u, value * 2u + 1u);
        }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fivestar-capture-" + Guid.NewGuid().ToString("N"));

    [DxcFact]
    public void A_capture_of_game_memory_replays_through_its_address_map()
    {
        var plan = new CapturePlan("CS_MemoryTest", UserData(), userConstants: false, DescriptorWindow.RecordsEnd) { Direct = new CapturedDispatch(1, 1, 1) };
        plan.Patches.Add((TableOffset, Record(InputAddress, Threads * sizeof(uint))));
        var input = Range(plan, InputAddress, 100);
        var output = Range(plan, OutputAddress, 102);
        CaptureCopies.Layout(plan);
        var readback = new byte[plan.ReadbackBytes];

        for (uint thread = 0; thread < Threads; thread++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(readback.AsSpan((int)(input.Before + (thread * sizeof(uint)))), thread * 5);
            BinaryPrimitives.WriteUInt32LittleEndian(readback.AsSpan((int)(input.After + (thread * sizeof(uint)))), thread * 5);
            BinaryPrimitives.WriteUInt32LittleEndian(readback.AsSpan((int)(output.After + (thread * sizeof(uint)))), (thread * 10) + 1);
        }

        var capture = CaptureOutput.Write(plan, _folder, readback);
        var folder = new CaptureFolder(_folder);
        using var device = GpuDevice.CreateWarp(debugLayer: false);

        var outcome = new DispatchReplay(device.Device).Run(folder, capture, Compile(), 1);

        foreach (var resource in capture.Resources.Where(resource => resource.Output is not null))
        {
            var difference = OutputComparison.Compare(resource, folder.ReadFile(resource.Output!), outcome.Outputs[resource.Id], null, 4);
            Assert.True(difference.Same, $"{resource.Label}: {difference.Differing} dwords differ, first at {string.Join(", ", difference.FirstOffsets)}");
        }

        Assert.Equal(2, capture.Resources.Count(resource => resource.Output is not null));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static uint[] UserData()
    {
        var data = new uint[CommandListBindings.UserDataDwords];
        data[4] = TableOffset;
        data[6] = unchecked((uint)OutputAddress);
        data[7] = (uint)(OutputAddress >> 32);

        return data;
    }

    /// <summary>A raw V# over <paramref name="bytes"/> at <paramref name="address"/>, as the window mirror holds it.</summary>
    private static byte[] Record(ulong address, uint bytes)
    {
        var record = new byte[DescriptorWindow.ViewRecordBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)address);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), (uint)(address >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), bytes);

        return record;
    }

    /// <summary>One page of game memory, an output, with the view pair at <paramref name="pair"/>.</summary>
    private static CaptureSource Range(CapturePlan plan, ulong address, uint pair)
    {
        var shape = new CapturedResource($"memory-{plan.Mapped.Count}", $"memory 0x{address:x}", CapturedResource.BufferDimension, CaptureMemory.PageBytes, 1, 1, 1, 0, 0, CaptureMemory.PageBytes, null, null)
        {
            ConsoleAddress = address,
        };
        var source = new CaptureSource(shape, [], 0, 0) { Output = true };
        plan.Sources.Add(source);
        plan.Mapped.Add((source, pair));

        return source;
    }

    private byte[] Compile()
    {
        var work = Path.Combine(_folder, "compile");
        RuntimeIncludes.WriteTo(work);
        var path = Path.Combine(work, "program.hlsl");
        File.WriteAllText(path, Program);
        var compiler = new DxcCompiler(DxcOptions.Defaults(PipelineFactAttribute.Dxc, retry: false));

        var outcome = compiler.Compile(new CompileRequest(path, Program, "cs", DisableOptimizations: false), TimeSpan.FromMinutes(2));
        Assert.True(outcome.Succeeded, outcome.Error);

        return File.ReadAllBytes(Path.ChangeExtension(path, ".dxil"));
    }
}
