/* =============================================================================
 * bvr_config - settings read from a file, NOT from the environment.
 *
 * Environment variables cannot reach a game started by Steam or the TaleWorlds
 * launcher: those inherit the environment Steam itself was started with, so a
 * variable set in a shell is simply invisible. Every switch built for isolating
 * the Phase 4 crash was unusable for that reason.
 *
 * The file lives beside the game's own configs:
 *
 *     Documents\Mount and Blade II Bannerlord\Configs\BannerlordVR.cfg
 *
 * Format is key=value, one per line; '#' or ';' begins a comment. Missing file
 * or missing key means the default. The managed side reads the same file, so
 * both halves of the mod are configured in one place.
 * ========================================================================== */
#ifndef BVR_CONFIG_H
#define BVR_CONFIG_H

#include <stdint.h>

namespace bvr {

/* Reads and caches the file. Safe to call repeatedly. */
void config_load();

/* Absolute path of the config file, for logging. Valid after config_load(). */
const char* config_path();

bool  config_bool(const char* key, bool fallback);
float config_float(const char* key, float fallback);

} // namespace bvr

#endif /* BVR_CONFIG_H */
