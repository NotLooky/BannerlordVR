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
    /* No `mode` at all is engine-camera, as VrConfig.Mode says. Requiring the key
       here was TEST 540's broken render with no config: managed drew the
       symmetric frustum while this side submitted the runtime's asymmetric one. */
    auto mode = g_values.find("mode");
    if (mode != g_values.end() && !trim(mode->second).empty() &&
        lower(trim(mode->second)) != "engine-camera")
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

std::string narrow(const wchar_t* wide)
{
    char out[MAX_PATH] = {};
    WideCharToMultiByte(CP_UTF8, 0, wide, -1, out, sizeof(out), nullptr, nullptr);
    return out;
}

/* Later files override earlier ones key by key; -1 means the file is absent. */
int parse_file(const wchar_t* wide)
{
    FILE* file = nullptr;
    if (_wfopen_s(&file, wide, L"r") != 0 || file == nullptr)
        return -1;

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
    return count;
}

} // namespace

void config_load()
{
    if (g_loaded)
        return;
    g_loaded = true;

    /* The release ships its tested settings as BannerlordVR.default.cfg in the
       module root, two folders above this DLL. The player's own file goes on
       top, so it only has to hold what they changed, and a new release's
       defaults still reach a player who already has one. VrConfig layers the
       same two files the same way. */
    HMODULE self = nullptr;
    wchar_t shipped[MAX_PATH] = {};
    if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                               GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(&config_load), &self) &&
        GetModuleFileNameW(self, shipped, MAX_PATH) > 0)
    {
        for (int up = 0; up < 3; ++up)   /* file name, Win64_Shipping_Client, bin */
        {
            wchar_t* slash = wcsrchr(shipped, L'\\');
            if (slash != nullptr)
                *slash = L'\0';
        }
        wcscat_s(shipped, L"\\BannerlordVR.default.cfg");

        const int n = parse_file(shipped);
        if (n >= 0)
            BVR_INFO("Loaded %d shipped default(s) from %s.", n, narrow(shipped).c_str());
    }

    wchar_t docs[MAX_PATH] = {};
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_MYDOCUMENTS, nullptr, 0, docs)))
        return;

    wchar_t wide[MAX_PATH] = {};
    _snwprintf_s(wide, _TRUNCATE,
                 L"%s\\Mount and Blade II Bannerlord\\Configs\\BannerlordVR.cfg", docs);
    g_path = narrow(wide);

    const int count = parse_file(wide);
    if (count < 0)
        BVR_INFO("No config at %s; using defaults.", g_path.c_str());
    else
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

bool config_is(const char* key, const char* value)
{
    config_load();

    std::string v;
    return lookup(key, &v) && lower(v) == lower(value);
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
