// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

// The hardware wave a mesh shader runs in (MeshWaveProbe). Built with:
//   dxc -T ms_6_5 -E main -Qstrip_debug -Qstrip_reflect -Fo mesh_wave_probe.dxil mesh_wave_probe.hlsl
// u0 holds {least lane count, greatest lane count}; it starts as {0xffffffff, 0}.
RWByteAddressBuffer lanes : register(u0);

struct Vertex
{
    float4 position : SV_Position;
};

[numthreads(128, 1, 1)]
[outputtopology("triangle")]
void main(uint thread : SV_GroupIndex, out vertices Vertex vertices[1], out indices uint3 triangles[1])
{
    SetMeshOutputCounts(0, 0);

    // Never written: no vertex is output, but the validator asks for a write of every element.
    if (thread == 128u)
    {
        vertices[0].position = 0;
    }

    lanes.InterlockedMin(0, WaveGetLaneCount());
    lanes.InterlockedMax(4, WaveGetLaneCount());
}
