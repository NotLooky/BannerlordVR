#ifndef BVR_STEREO_SRV_RUNS_H
#define BVR_STEREO_SRV_RUNS_H
#include <stdint.h>

namespace bvr {
struct SrvRun { uint8_t first, last; };
struct SrvRuns
{
    // A new run starts at least nine slots after the previous run.
    uint8_t count;
    SrvRun runs[16];
};
inline bool srv_slot_declared(const uint64_t bits[2], int slot)
{
    return ((bits[slot >> 6] >> (slot & 63)) & 1ull) != 0;
}
// The existing gap-8 fetch rule, computed once per shader. This caches
// immutable declarations, never mutable context bindings or resource pointers.
inline SrvRuns build_srv_runs(const uint64_t bits[2])
{
    SrvRuns result = {};
    for (int lo = 0; lo < 128; ++lo)
    {
        if (!srv_slot_declared(bits, lo)) continue;
        int hi = lo;
        for (int look = lo + 1; look < 128 && look <= hi + 8; ++look)
            if (srv_slot_declared(bits, look)) hi = look;
        result.runs[result.count++] = { static_cast<uint8_t>(lo), static_cast<uint8_t>(hi) };
        lo = hi;
    }
    return result;
}
} // namespace bvr
#endif
