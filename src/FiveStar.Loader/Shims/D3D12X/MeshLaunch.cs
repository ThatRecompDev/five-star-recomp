// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using FiveStar.Graphics.Pipelines;
using FiveStar.Loader.Native;
using FiveStar.Shaders.Compilation;
using FiveStar.Shaders.Dxil.Container;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// <c>--mesh-launch</c>: mesh draws launched by the GPU. An amplification shader of ours sits in front of every
/// translated mesh program; for an ExecuteIndirect of one mesh record without a count buffer it reads the record where
/// the game's passes wrote it, bounds it as <see cref="IndirectClamp"/> bounds a mesh record (65,535 groups, one
/// dimension), charges the list's work budget the same way (a record that does not fit runs empty) and launches the
/// mesh groups: one DispatchMesh(1, 1, 1) with three root arguments in register space <see cref="BindlessRootSignature.LaunchSpace"/>, and no compute
/// pass or barrier of ours between the draws. The clamp's pass cost about 14 us per indirect mesh draw against 4 us for
/// a direct one, and its transitions emptied the GPU between draws. A call with a count buffer or several records is
/// clamped by <see cref="IndirectClamp"/> as before and then launched record by record from the clamped copy.
/// Translated mesh programs declare the 4-byte payload the stage passes (TranslationOptions.MeshPayload); a pipeline
/// gets the stage only when all its mesh programs do.
/// The design and its facts follow Kelvaroq's patch 0041 (mesh launch, GPL-3.0, github.com/fablinoo/kelvaroq; see
/// THIRD_PARTY_NOTICES.md), here with our own clamp's bounds and budget.
/// </summary>
internal static unsafe class MeshLaunch
{
    /// <summary>The payload every translated mesh program declares and never reads.</summary>
    public const int PayloadBytes = 4;

    /// <summary>The stage reads the record at its root SRV, bounds it and charges the budget.</summary>
    public const uint ChargedRecord = 0xFFFFFFFFu;

    /// <summary>The stage reads a record <see cref="IndirectClamp"/> already bounded and charged.</summary>
    public const uint ClampedRecord = 0xFFFFFFFEu;

    /// <summary>ID3D12GraphicsCommandList SetGraphicsRoot32BitConstant, SetGraphicsRootShaderResourceView, SetGraphicsRootUnorderedAccessView; ID3D12GraphicsCommandList6::DispatchMesh.</summary>
    private const int SetConstantSlot = 34;
    private const int SetShaderResourceSlot = 40;
    private const int SetUnorderedAccessSlot = 42;
    private const int DispatchMeshSlot = 79;

    /// <summary>ID3D12Object GetPrivateData and SetPrivateData.</summary>
    private const int GetPrivateDataSlot = 3;
    private const int SetPrivateDataSlot = 4;

    private const string Profile = "as";

    // DXBC: chunk count @28, chunk offsets from @32; a chunk is its four-character code, its size, then its data.
    // PSV0 data: the runtime info's size, then the info: MS payload bytes @8, the stage @24.
    private const int ChunkCountOffset = 28;
    private const int ChunkOffsetsOffset = 32;
    private const int ChunkHeaderBytes = 8;
    private const int PayloadField = 8;
    private const int StageField = 24;
    private const uint PsvCode = 0x30565350;

    /// <summary>Marks a desktop pipeline that has the stage (its private data).</summary>
    private static readonly Guid LaunchMark = new("5f3b8f0e-2a63-4d51-9f43-6b1c7b0d4a91");

    private static long _launched;
    private static long _fromClamp;
    private static long _dropped;

    /// <summary>The source of the stage; its root arguments are <see cref="BindlessRootSignature"/>'s launch parameters.</summary>
    public const string Source = """
        // Mesh launch (MeshLaunch.cs), after Kelvaroq's launch.hlsl (GPL-3.0, github.com/fablinoo/kelvaroq).
        struct LaunchPayload { uint unused; };
        groupshared LaunchPayload launchPayload;
        cbuffer Launch : register(b0, space10) { uint mode; };
        ByteAddressBuffer record : register(t0, space10);
        RWByteAddressBuffer budget : register(u0, space10);
        static const uint ChargedRecord = 0xffffffffu;
        static const uint MaxGroupsPerDimension = 65535u;
        static const uint ListBudget = 1u << 27;
        static const uint GroupWork = 128u;
        static const uint Tries = 64u;
        // As IndirectClamp charges a record: a compare-exchange, so the count never wraps.
        bool Charge(uint work) {
            if (work == 0u) return true;
            if (work > ListBudget) return false;
            uint seen;
            budget.InterlockedAdd(0, 0u, seen);
            [loop] for (uint i = 0; i < Tries; i++) {
                if (seen > ListBudget - work) return false;
                uint original;
                budget.InterlockedCompareExchange(0, seen, seen + work, original);
                if (original == seen) return true;
                seen = original;
            }
            return false;
        }
        [numthreads(1, 1, 1)]
        void main() {
            uint3 groups = record.Load3(0);
            if (mode == ChargedRecord) {
                if (groups.x > MaxGroupsPerDimension) groups = uint3(0u, 0u, 0u);
                groups = uint3(groups.x, min(groups.y, 1u), min(groups.z, 1u));
                if (!Charge(groups.x * groups.y * groups.z * GroupWork)) groups = uint3(0u, 0u, 0u);
            }
            launchPayload.unused = 0u;
            DispatchMesh(groups.x, groups.y, groups.z, launchPayload);
        }
        """;

    /// <summary>Whether mesh pipelines get the stage (<c>--mesh-launch</c>); the root signature carries its parameters.</summary>
    public static bool Enabled { get; set; }

    /// <summary>The compiled stage; null until <see cref="Prepare"/> made it, or when it could not.</summary>
    public static byte[]? Code { get; private set; }

    /// <summary>Mesh records launched by the stage, of them those <see cref="IndirectClamp"/> had clamped, and mesh calls dropped (no clamp to run).</summary>
    public static (long Launched, long FromClamp, long Dropped) Counts => (Interlocked.Read(ref _launched), Interlocked.Read(ref _fromClamp), Interlocked.Read(ref _dropped));

    /// <summary>A mesh call on a pipeline with the stage that neither the stage nor the clamp could run.</summary>
    public static void Drop()
    {
        Interlocked.Increment(ref _dropped);
    }

    /// <summary>
    /// Compiles the stage with <paramref name="compiler"/> (dxc, shader model 6.5 at least) when enabled; what came of it
    /// for the log, empty when disabled. Without it mesh pipelines get no stage and their draws are clamped as before.
    /// </summary>
    public static string Prepare(DxcLibraryCompiler compiler, string folder)
    {
        if (!Enabled)
        {
            return string.Empty;
        }

        var (dxil, messages) = compiler.CompileBytes(new CompileRequest(Path.Combine(folder, "mesh_launch.hlsl"), Source, Profile, DisableOptimizations: false));
        Code = dxil;

        if (dxil is null)
        {
            return $"; --mesh-launch: the amplification stage did not compile, mesh draws are clamped as before ({messages.Trim()})";
        }

        return $"; mesh draws launched by an amplification stage ({dxil.Length} bytes of DXIL)";
    }

    /// <summary>Sets the compiled stage directly (tests).</summary>
    public static void Use(byte[]? code)
    {
        Code = code;
    }

    /// <summary>Whether <paramref name="dxil"/> is a mesh program that takes the stage's payload.</summary>
    public static bool Accepts(ReadOnlySpan<byte> dxil)
    {
        var psv = Chunk(dxil, PsvCode);

        if (psv.Length < sizeof(uint) + StageField + 1)
        {
            return false;
        }

        var info = psv[sizeof(uint)..];

        return info[StageField] == (byte)DxilShaderKind.Mesh && BinaryPrimitives.ReadUInt32LittleEndian(info[PayloadField..]) == PayloadBytes;
    }

    /// <summary>Notes that desktop <paramref name="pipeline"/> was made with the stage.</summary>
    public static void Mark(nint pipeline)
    {
        if (pipeline == 0)
        {
            return;
        }

        uint one = 1;
        Guid mark = LaunchMark;
        _ = ((delegate* unmanaged<nint, Guid*, uint, void*, int>)ComObject.Slot(pipeline, SetPrivateDataSlot))(pipeline, &mark, sizeof(uint), &one);
    }

    /// <summary>Whether desktop <paramref name="pipeline"/> was made with the stage.</summary>
    public static bool Has(nint pipeline)
    {
        if (pipeline == 0)
        {
            return false;
        }

        uint value = 0;
        uint size = sizeof(uint);
        Guid mark = LaunchMark;

        return ((delegate* unmanaged<nint, Guid*, uint*, void*, int>)ComObject.Slot(pipeline, GetPrivateDataSlot))(pipeline, &mark, &size, &value) >= 0 && value == 1;
    }

    /// <summary>
    /// Records one launch on desktop list <paramref name="list"/>, whose graphics root signature is the bindless one and
    /// whose pipeline has the stage: the record at GPU address <paramref name="record"/> in <paramref name="mode"/>
    /// (<see cref="ChargedRecord"/> or <see cref="ClampedRecord"/>), the budget at <paramref name="budget"/>.
    /// </summary>
    [HotPath]
    public static void Record(nint list, uint mode, ulong record, ulong budget)
    {
        ((delegate* unmanaged<nint, uint, uint, uint, void>)ComObject.Slot(list, SetConstantSlot))(list, BindlessRootSignature.LaunchModeParameter, mode, 0);
        ((delegate* unmanaged<nint, uint, ulong, void>)ComObject.Slot(list, SetShaderResourceSlot))(list, BindlessRootSignature.LaunchRecordParameter, record);
        ((delegate* unmanaged<nint, uint, ulong, void>)ComObject.Slot(list, SetUnorderedAccessSlot))(list, BindlessRootSignature.LaunchBudgetParameter, budget);
        ((delegate* unmanaged<nint, uint, uint, uint, void>)ComObject.Slot(list, DispatchMeshSlot))(list, 1, 1, 1);
        Interlocked.Increment(ref _launched);

        if (mode == ClampedRecord)
        {
            Interlocked.Increment(ref _fromClamp);
        }
    }

    /// <summary>The data of the chunk <paramref name="code"/> of a DXBC container; empty when there is none.</summary>
    private static ReadOnlySpan<byte> Chunk(ReadOnlySpan<byte> container, uint code)
    {
        if (container.Length < ChunkOffsetsOffset)
        {
            return [];
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(container[ChunkCountOffset..]);

        for (int index = 0; index < count && ChunkOffsetsOffset + ((index + 1) * sizeof(uint)) <= container.Length; index++)
        {
            int at = BinaryPrimitives.ReadInt32LittleEndian(container[(ChunkOffsetsOffset + (index * sizeof(uint)))..]);

            if (at < 0 || at + ChunkHeaderBytes > container.Length)
            {
                continue;
            }

            int size = BinaryPrimitives.ReadInt32LittleEndian(container[(at + sizeof(uint))..]);

            if (BinaryPrimitives.ReadUInt32LittleEndian(container[at..]) == code && size >= 0 && at + ChunkHeaderBytes + size <= container.Length)
            {
                return container.Slice(at + ChunkHeaderBytes, size);
            }
        }

        return [];
    }
}
