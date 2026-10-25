#include "stereo_srv_runs.h"
#include <cstdio>
#include <vector>
#include <random>
#include <cstdlib>

static unsigned cases = 0;
static void check(const uint64_t bits[2])
{
    // Independent reference: walk declared slots, split where their distance
    // exceeds eight. This describes the old GetShaderResources call ranges.
    std::vector<std::pair<int, int>> expected;
    for (int i = 0; i < 128; ++i)
        if (bits[i / 64] & (uint64_t(1) << (i % 64)))
        {
            if (expected.empty() || i - expected.back().second > 8)
                expected.emplace_back(i, i);
            else expected.back().second = i;
        }
    const bvr::SrvRuns got = bvr::build_srv_runs(bits);
    if (got.count != expected.size() || got.count > 16) std::abort();
    for (unsigned r = 0; r < got.count; ++r)
        if (got.runs[r].first != expected[r].first || got.runs[r].last != expected[r].second)
            std::abort();
    ++cases;
}
int main()
{
    // Exhaust every 16-slot pattern at each edge and across the 64-bit boundary.
    for (int offset : { 0, 56, 112 })
        for (unsigned mask = 0; mask < 65536; ++mask)
        {
            uint64_t bits[2] = {};
            for (int i = 0; i < 16; ++i)
                if (mask & (1u << i)) bits[(offset + i) / 64] |= uint64_t(1) << ((offset + i) % 64);
            check(bits);
        }
    std::mt19937_64 rng(20261014);
    for (unsigned i = 0; i < 100000; ++i)
    {
        uint64_t bits[2] = { rng(), rng() };
        if (i & 1) { bits[0] &= rng() & rng(); bits[1] &= rng() & rng(); }
        check(bits);
    }
    for (int spacing = 1; spacing <= 16; ++spacing)
    {
        uint64_t bits[2] = {};
        for (int i = 0; i < 128; i += spacing) bits[i / 64] |= uint64_t(1) << (i % 64);
        check(bits);
    }
    std::printf("PASS: %u declaration patterns; identical resource-fetch ranges.\n", cases);
}
