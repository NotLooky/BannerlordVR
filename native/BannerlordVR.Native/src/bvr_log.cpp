#include "bvr_log.h"

#include <windows.h>
#include <shlobj.h>

#include <cstdarg>
#include <cstdio>
#include <mutex>
#include <share.h>

namespace bvr {
namespace {

FILE* g_file = nullptr;
std::mutex g_mutex;

} // namespace

void log_open()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_file != nullptr)
        return;

    wchar_t docs[MAX_PATH] = {};
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_MYDOCUMENTS, nullptr, 0, docs)))
        return;

    wchar_t dir[MAX_PATH] = {};
    _snwprintf_s(dir, _TRUNCATE, L"%s\\Mount and Blade II Bannerlord\\Logs", docs);
    CreateDirectoryW(dir, nullptr);

    wchar_t path[MAX_PATH] = {};
    _snwprintf_s(path, _TRUNCATE, L"%s\\BannerlordVR.Native.log", dir);

    /* KEEP THE PREVIOUS RUN.
     *
     * This opened with "w" and truncated on every launch, which is fine right
     * up until the interesting run is the one before last. A crash is followed
     * by a relaunch - that is what a player does next - and the relaunch was
     * erasing the only record of what crashed. It cost a real diagnosis: a
     * headset that crashed on startup left nothing behind, because the next
     * session on a different headset overwrote it before anyone looked.
     *
     * One generation is enough. The failure being chased is almost always in
     * the run just before the one that noticed it. */
    wchar_t previous[MAX_PATH] = {};
    _snwprintf_s(previous, _TRUNCATE, L"%s\\BannerlordVR.Native.prev.log", dir);
    DeleteFileW(previous);
    MoveFileW(path, previous);

    /* _wfsopen with _SH_DENYWR, NOT _wfopen_s.
     *
     * fopen_s and _wfopen_s open a file for EXCLUSIVE access on Windows - no
     * other handle at all, not even a reader. That made this log unreadable
     * while the game was running, so every number in it - the pose age, the
     * head-to-world gain, the held-image age, the AFW synthesis counts - could
     * only be read after the session that produced it had ended. The one thing
     * a latency instrument is for is watching it move while you move.
     *
     * _SH_DENYWR keeps the WRITE exclusive, which is the half that matters:
     * nothing else may write here, so the log cannot be interleaved or
     * truncated underneath us. Readers are let in. */
    g_file = _wfsopen(path, L"w", _SH_DENYWR);

    if (g_file != nullptr)
    {
        SYSTEMTIME st;
        GetLocalTime(&st);
        std::fprintf(g_file, "=== BannerlordVR.Native log opened %04d-%02d-%02d %02d:%02d:%02d ===\n",
                     st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
        std::fflush(g_file);
    }
}

void log_close()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_file != nullptr)
    {
        std::fclose(g_file);
        g_file = nullptr;
    }
}

void logf(const char* level, const char* fmt, ...)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_file == nullptr)
        return;

    SYSTEMTIME st;
    GetLocalTime(&st);
    std::fprintf(g_file, "%02d:%02d:%02d.%03d [%s] ",
                 st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, level);

    va_list args;
    va_start(args, fmt);
    std::vfprintf(g_file, fmt, args);
    va_end(args);

    std::fputc('\n', g_file);

    /* Flushed every line on purpose: when this crashes the game, the last line
       written is the only diagnostic that survives. */
    std::fflush(g_file);
}

} // namespace bvr
