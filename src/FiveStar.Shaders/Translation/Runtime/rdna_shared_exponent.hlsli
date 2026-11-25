// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later
// Filtered reads of shared-exponent light (R9G9B9E5) the GPU wrote into an R32_UINT texture, which the desktop can only
// view in R32_UINT (typed image views, class 3): texels loaded as bits, decoded, then filtered as the sampler says.
// Written into the programs listed for it (TranslationOptions.SharedExponentPrograms), not included by every program,
// so a change here leaves every other program's cache keys as they are.
// The sampler word (a literal S# packed by the translator, a heap sampler's record dword 3 written by the loader):
// bits 0-2 / 3-5 the U / V address mode (SQ_TEX_CLAMP: 0 wrap, 1 mirror, 3/5/7 mirror once, others clamp; borders read
// as the edge; a volume's W axis takes V's mode), 6 mag linear, 7 min linear, 8-9 mip filter (0 none, 1 point,
// 2 linear), 10-17 max LOD and 18-25 min LOD (u4.4), 26-30 LOD bias (s3.2), 31 set when the word is known (else
// bilinear, linear mips, clamp, no LOD limits).
int se_address(int i, int n, uint mode) {
    if (mode == 0u) return ((i % n) + n) % n;
    if (mode == 1u) { int p = ((i % (2 * n)) + 2 * n) % (2 * n); return p < n ? p : 2 * n - 1 - p; }
    if (mode == 3u || mode == 5u || mode == 7u) i = i < 0 ? -1 - i : i;
    return clamp(i, 0, n - 1);
}
float3 se_decode(uint b) {
    return float3(b & 0x1ffu, (b >> 9) & 0x1ffu, (b >> 18) & 0x1ffu) * exp2(float(int(b >> 27) - 24));
}
uint se_word(uint s) { return (s >> 31) == 0u ? 0x8003c2d2u : s; }
// The level-of-detail clamps and filters of a sample: the clamped LOD, whether it filters linearly, the mip filter.
float se_clamp_lod(float lod, uint s, uint levels) {
    float bias = float(int(s << 1) >> 27) / 4.0;
    float low = float((s >> 18) & 0xffu) / 16.0;
    float high = min(float((s >> 10) & 0xffu) / 16.0, float(levels - 1u));
    return clamp(lod + bias, low, max(high, low));
}
bool se_filtered(float lod, uint s) { return ((lod > 0.0 ? (s >> 7) : (s >> 6)) & 1u) != 0; }

float3 se_texel(Texture2D<uint4> t, int2 p, int2 size, uint level, uint s) {
    int2 q = int2(se_address(p.x, size.x, s & 7u), se_address(p.y, size.y, (s >> 3) & 7u));
    return se_decode(t.Load(int3(q, (int)level)).x);
}
float3 se_level(Texture2D<uint4> t, float2 uv, uint level, bool filtered, uint s, int2 offset) {
    uint w, h, n; t.GetDimensions(level, w, h, n);
    int2 size = int2(w, h);
    float2 p = uv * float2(size);
    if (!filtered) return se_texel(t, int2(floor(p)) + offset, size, level, s);
    p -= 0.5;
    int2 i = int2(floor(p)) + offset;
    float2 f = p - floor(p);
    float3 top = lerp(se_texel(t, i, size, level, s), se_texel(t, i + int2(1, 0), size, level, s), f.x);
    float3 bottom = lerp(se_texel(t, i + int2(0, 1), size, level, s), se_texel(t, i + int2(1, 1), size, level, s), f.x);
    return lerp(top, bottom, f.y);
}
float4 se_sample(Texture2D<uint4> t, float2 uv, float lod, uint s, int2 offset) {
    s = se_word(s);
    uint w, h, levels; t.GetDimensions(0u, w, h, levels);
    lod = se_clamp_lod(lod, s, levels);
    bool filtered = se_filtered(lod, s);
    uint mip = (s >> 8) & 3u;
    if (mip == 2u) {
        uint l0 = min((uint)floor(lod), levels - 1u);
        uint l1 = min(l0 + 1u, levels - 1u);
        return float4(lerp(se_level(t, uv, l0, filtered, s, offset), se_level(t, uv, l1, filtered, s, offset), lod - floor(lod)), 1.0);
    }
    uint level = mip == 0u ? 0u : min((uint)floor(lod + 0.5), levels - 1u);
    return float4(se_level(t, uv, level, filtered, s, offset), 1.0);
}

float3 se_texel3(Texture3D<uint4> t, int3 p, int3 size, uint level, uint s) {
    uint v = (s >> 3) & 7u;
    int3 q = int3(se_address(p.x, size.x, s & 7u), se_address(p.y, size.y, v), se_address(p.z, size.z, v));
    return se_decode(t.Load(int4(q, (int)level)).x);
}
float3 se_level3(Texture3D<uint4> t, float3 uvw, uint level, bool filtered, uint s, int3 offset) {
    uint w, h, d, n; t.GetDimensions(level, w, h, d, n);
    int3 size = int3(w, h, d);
    float3 p = uvw * float3(size);
    if (!filtered) return se_texel3(t, int3(floor(p)) + offset, size, level, s);
    p -= 0.5;
    int3 i = int3(floor(p)) + offset;
    float3 f = p - floor(p);
    float3 near0 = lerp(se_texel3(t, i, size, level, s), se_texel3(t, i + int3(1, 0, 0), size, level, s), f.x);
    float3 near1 = lerp(se_texel3(t, i + int3(0, 1, 0), size, level, s), se_texel3(t, i + int3(1, 1, 0), size, level, s), f.x);
    float3 far0 = lerp(se_texel3(t, i + int3(0, 0, 1), size, level, s), se_texel3(t, i + int3(1, 0, 1), size, level, s), f.x);
    float3 far1 = lerp(se_texel3(t, i + int3(0, 1, 1), size, level, s), se_texel3(t, i + int3(1, 1, 1), size, level, s), f.x);
    return lerp(lerp(near0, near1, f.y), lerp(far0, far1, f.y), f.z);
}
float4 se_sample3(Texture3D<uint4> t, float3 uvw, float lod, uint s, int3 offset) {
    s = se_word(s);
    uint w, h, d, levels; t.GetDimensions(0u, w, h, d, levels);
    lod = se_clamp_lod(lod, s, levels);
    bool filtered = se_filtered(lod, s);
    uint mip = (s >> 8) & 3u;
    if (mip == 2u) {
        uint l0 = min((uint)floor(lod), levels - 1u);
        uint l1 = min(l0 + 1u, levels - 1u);
        return float4(lerp(se_level3(t, uvw, l0, filtered, s, offset), se_level3(t, uvw, l1, filtered, s, offset), lod - floor(lod)), 1.0);
    }
    uint level = mip == 0u ? 0u : min((uint)floor(lod + 0.5), levels - 1u);
    return float4(se_level3(t, uvw, level, filtered, s, offset), 1.0);
}

// The level an implicit or gradient sample takes on a texture of <size> texels: log2 of the larger footprint.
float se_lod(float2 dx, float2 dy, float2 size) { return log2(max(length(dx * size), length(dy * size))); }
float se_lod3(float3 dx, float3 dy, float3 size) { return log2(max(length(dx * size), length(dy * size))); }
