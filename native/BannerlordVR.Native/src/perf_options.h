#ifndef BVR_PERF_OPTIONS_H
#define BVR_PERF_OPTIONS_H
#include "bvr_config.h"

namespace bvr {
// Read once after config loading. Switches require a restart. The legacy
// switch restores previous paths without replacing the DLL.
inline bool perf_legacy()
{
    static const bool value = config_bool("perf_legacy", false);
    return value;
}
inline bool perf_diagnostics()
{
    static const bool value = perf_legacy() || config_bool("perf_diagnostics", false);
    return value;
}
inline bool perf_shader_runs()
{
    static const bool value = !perf_legacy() && config_bool("perf_shader_runs", true);
    return value;
}
inline bool perf_lazy_sharpen()
{
    static const bool value = !perf_legacy() && config_bool("perf_lazy_sharpen", true);
    return value;
}
inline bool perf_direct_submit()
{
    static const bool value = !perf_legacy() && config_bool("perf_direct_submit", true);
    return value;
}
} // namespace bvr
#endif
