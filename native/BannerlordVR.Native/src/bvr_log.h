/* Native-side logging. Separate file from the managed log because this runs on
 * the render thread and must not contend with the C# writer for a file handle.
 * -> Documents\Mount and Blade II Bannerlord\Logs\BannerlordVR.Native.log      */
#ifndef BVR_LOG_H
#define BVR_LOG_H

namespace bvr {

void log_open();
void log_close();

/* printf-style. Never throws, never allocates on the failure path. */
void logf(const char* level, const char* fmt, ...);

/* Push whatever is buffered to disk now. Called by the crash handler, which is
   the one place that cannot wait for the next timed flush. */
void log_flush();

} // namespace bvr

#define BVR_INFO(...)  ::bvr::logf("INFO ", __VA_ARGS__)
#define BVR_WARN(...)  ::bvr::logf("WARN ", __VA_ARGS__)
#define BVR_ERR(...)   ::bvr::logf("ERROR", __VA_ARGS__)

#endif /* BVR_LOG_H */
