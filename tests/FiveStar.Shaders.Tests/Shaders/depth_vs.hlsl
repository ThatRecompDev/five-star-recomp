// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later
// The fullscreen triangle of fullscreen_vs.hlsl at depth 0.5, for depth-export tests.
float4 main(uint vid : SV_VertexID) : SV_Position {
    float2 uv = float2((vid << 1) & 2, vid & 2);
    return float4(uv * float2(1, -1) + float2(-1, 1), 0.5, 1);
}