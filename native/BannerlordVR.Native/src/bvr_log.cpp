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

/* How long a routine INFO line may sit in the buffer before it reaches disk. */
const ULONGLONG kFlushEveryMs = 250;
const size_t    kBufferBytes  = 64 * 1024;

ULONGLONG g_lastFlush = 0;

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
     * head-to-world gain, the held-image age, the DR synthesis counts - could
     * only be read after the session that produced it had ended. The one thing
     * a latency instrument is for is watching it move while you move.
     *
     * _SH_DENYWR keeps the WRITE exclusive, which is the half that matters:
     * nothing else may write here, so the log cannot be interleaved or
     * truncated underneath us. Readers are let in. */
    g_file = _wfsopen(path, L"w", _SH_DENYWR);

    if (g_file != nullptr)
    {
        /* Buffered on purpose - see the comment in logf. 64 KB holds several
           seconds of the busiest reporting second measured (119 lines). */
        std::setvbuf(g_file, nullptr, _IOFBF, kBufferBytes);

        SYSTEMTIME st;
        GetLocalTime(&st);
        std::fprintf(g_file, "=== BannerlordVR.Native log opened %04d-%02d-%02d %02d:%02d:%02d ===\n",
                     st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
        std::fflush(g_file);
        g_lastFlush = GetTickCount64();
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

    /* NOT FLUSHED EVERY LINE ANY MORE.
     *
     * It used to be, so that a crash kept its last line. The cost of that was
     * only visible once native stereo made the reporting dense: these calls
     * come from inside the Present hook, and a 2026-09-17 native-stereo run
     * averaged 23 lines a second with 119 in its worst second - 119 synchronous
     * disk writes landing on the render thread inside one second, which is the
     * exact shape of a once-a-second hitch.
     *
     * The guarantee is kept where it actually matters instead of everywhere:
     *
     *   - WARN and ERROR flush immediately, as before. They are rare, and a
     *     crash report is written entirely with BVR_ERR.
     *   - The crash handler calls log_flush() before it returns.
     *   - Routine INFO lines flush on a 250 ms timer, so a reader tailing the
     *     file (tools\vrwatch.ps1) still sees them arrive live.
     *
     * What is genuinely at risk now is up to 250 ms of INFO lines, and only if
     * the process dies without reaching our handler at all. */
    const bool urgent = (level[0] != 'I');   /* "INFO " vs "WARN "/"ERROR" */
    const ULONGLONG now = GetTickCount64();

    if (urgent || now - g_lastFlush >= kFlushEveryMs)
    {
        std::fflush(g_file);
        g_lastFlush = now;
    }
}

void log_flush()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_file != nullptr)
    {
        std::fflush(g_file);
        g_lastFlush = GetTickCount64();
    }
}

} // namespace bvr
