// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later
// Cross-lane reads of a pixel program that see only running lanes. A pixel wave the desktop fills with fewer pixels
// than it has lanes leaves the rest unlaunched, and a read of such a lane is undefined (it returns whatever its
// registers last held). On the console those lanes run, in whole-wave mode, with what the program gives a lane without
// a pixel: typically the identity of the reduction it computes (all ones for a minimum, 0 for a maximum or an or). A
// read of an unlaunched lane here returns the reader's own value, which a minimum, maximum, and or or leaves as it is,
// and vw_read_fill_live its known fill. VIctory reads these reductions as WaveActive operations over the running lanes
// (facts only). Written into the pixel programs that read across lanes (ProgramEmitter), not included by every
// program, so the prelude and every other program's cache keys stay as they are.
bool vw_live(uint lane) {
    uint4 live = WaveActiveBallot(true);
    return lane < 128u && ((live[lane >> 5] >> (lane & 31u)) & 1u) != 0u;
}
uint vw_read_live(uint x, uint lane) {
#if VW_GROUP_WAVE
    return vw_group_read(x, lane);
#elif VW_EMULATE
    lane &= VW_WAVE - 1u;
    if (!VW_GROUP_LANES && WaveGetLaneCount() == VW_WAVE) return vw_live(lane) ? WaveReadLaneAt(x, lane) : x;
    uint hw = vw_origin() + lane, count = WaveGetLaneCount();
    uint v = WaveReadLaneAt(x, min(hw, count - 1u));
    return hw >= count ? 0u : vw_live(hw) ? v : x;
#else
    return vw_live(lane) ? WaveReadLaneAt(x, lane) : x;
#endif
}
uint vw_read_fill_live(uint x, uint lane, uint fill) {
#if VW_EMULATE && !VW_GROUP_LANES
    lane &= VW_WAVE - 1u;
    uint hw = vw_origin() + lane, count = WaveGetLaneCount();
    uint v = WaveReadLaneAt(x, min(hw, count - 1u));
    return hw < count && vw_live(hw) ? v : fill;
#else
    return vw_read_live(x, lane);
#endif
}
