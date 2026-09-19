#include "bvr_config.h"
#include "bvr_log.h"

#include <windows.h>
#include <shlobj.h>

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <unordered_map>

namespace bvr {
namespace {

std::unordered_map<std::string, std::string> g_values;
std::string g_path;
bool g_loaded = false;

std::string trim(const std::string& s)
{
    const size_t first = s.find_first_not_of(" \t\r\n");
    if (first == std::string::npos)
        return std::string();

    const size_t last = s.find_last_not_of(" \t\r\n");
    return s.substr(first, last - first + 1);
}

std::string lower(std::string s)
{
    for (char& c : s)
        c = static_cast<char>(::tolower(static_cast<unsigned char>(c)));
    return s;
}

/* Mode defaults - the mirror of VrConfig's table, and it exists for one key.
 *
 * `mode = engine-camera` moves the ENGINE's own camera to the eye and gives it
 * the headset frustum, which leaves the view-projection patcher with nothing to
 * correct. Both halves of the mod read this same file, so both have to reach the
 * same conclusion about `vp_patch` from it; a managed-only rule would leave the
 * native side patching matrices underneath a camera that is already right.
 *
 * Only keys BOTH sides read belong here. Everything else the mode implies is
 * managed-only and lives in VrConfig. */
bool mode_default(const std::string& key, std::string* out)
{
    auto mode = g_values.find("mode");
    if (mode == g_values.end() || lower(trim(mode->second)) != "engine-camera")
        return false;

    if (key == "vp_patch")
    {
        *out = "0";
        return true;
    }

    /* Under alternate-frame rendering the engine draws one eye per frame through
       a single camera, so a per-eye asymmetric frustum would flip the projection
       every frame. A symmetric frustum containing the headset's asymmetric one is
       identical for both eyes and constant across frames, so the two images
       cannot fail to line up. The managed side computes the containing frustum
       from the same rule, and submitted_fov() below reads this key - both have to
       reach the same conclusion or the compositor places an image somewhere other
       than where it was drawn. */
    if (key == "symmetric_fov")
    {
        *out = "1";
        return true;
    }

    return false;
}

/* Explicit value first, then the mode's default. A key written by hand always
   wins, so one line can still be pulled out of a mode for a comparison run. */
bool lookup(const char* key, std::string* out)
{
    const std::string k = lower(key);

    auto it = g_values.find(k);
    if (it != g_values.end() && !it->second.empty())
    {
        *out = it->second;
        return true;
    }

    return mode_default(k, out);
}

} // namespace

void config_load()
{
    if (g_loaded)
        return;
    g_loaded = true;

    wchar_t docs[MAX_PATH] = {};
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_MYDOCUMENTS, nullptr, 0, docs)))
        return;

    wchar_t wide[MAX_PATH] = {};
    _snwprintf_s(wide, _TRUNCATE,
                 L"%s\\Mount and Blade II Bannerlord\\Configs\\BannerlordVR.cfg", docs);

    char narrow[MAX_PATH] = {};
    WideCharToMultiByte(CP_UTF8, 0, wide, -1, narrow, sizeof(narrow), nullptr, nullptr);
    g_path = narrow;

    FILE* file = nullptr;
    if (_wfopen_s(&file, wide, L"r") != 0 || file == nullptr)
    {
        BVR_INFO("No config at %s; using defaults.", g_path.c_str());
        return;
    }

    char line[512] = {};
    int count = 0;
    while (std::fgets(line, sizeof(line), file) != nullptr)
    {
        std::string text = trim(line);
        if (text.empty() || text[0] == '#' || text[0] == ';')
            continue;

        const size_t eq = text.find('=');
        if (eq == std::string::npos)
            continue;

        const std::string key = lower(trim(text.substr(0, eq)));
        const std::string value = trim(text.substr(eq + 1));
        if (key.empty())
            continue;

        g_values[key] = value;
        ++count;
    }

    std::fclose(file);
    BVR_INFO("Loaded %d setting(s) from %s.", count, g_path.c_str());

    auto mode = g_values.find("mode");
    if (mode != g_values.end())
        BVR_INFO("Config mode = %s.", trim(mode->second).c_str());
}

const char* config_path()
{
    return g_path.c_str();
}

bool config_bool(const char* key, bool fallback)
{
    config_load();

    std::string value;
    if (!lookup(key, &value))
        return fallback;

    const std::string v = lower(value);
    return !(v == "0" || v == "false" || v == "off" || v == "no");
}

float config_float(const char* key, float fallback)
{
    config_load();

    std::string value;
    if (!lookup(key, &value))
        return fallback;

    return static_cast<float>(std::atof(value.c_str()));
}

} // namespace bvr
