/* =============================================================================
 * bvr_crash - turns a bare fault offset into a call stack.
 *
 * Windows has been reporting every crash identically:
 *
 *     faulting module  TaleWorlds.Native.dll
 *     exception        0xC0000005
 *     fault offset     0x323c14
 *
 * Deterministic, inside rgl, and completely opaque - the same offset appeared
 * whether the anchor came from the agent or the camera, whether contour was on
 * or off, and whether the battle had 100 agents a side or the minimum. Five
 * theories eliminated and still no idea WHY.
 *
 * A vectored exception handler runs on the faulting thread with its frames
 * still intact, so it can walk the stack and report who called into rgl. There
 * are no PDBs for the engine, so frames come out as module+RVA - but the chain
 * of modules alone says whether the fault is reached through rgl's own render
 * loop, through our Present hook, or from somewhere else entirely.
 * ========================================================================== */
#ifndef BVR_CRASH_H
#define BVR_CRASH_H

namespace bvr {

/* Installs the handler. Safe to call more than once. */
void install_crash_logger();
void remove_crash_logger();

/* Records which piece of managed VR code was last entered, and when. Read back
   by the crash handler - see the note on bvr_breadcrumb in bvr_api.h for why
   the native stack scan cannot answer this on its own. */
void set_breadcrumb(int id);

} // namespace bvr

#endif /* BVR_CRASH_H */
