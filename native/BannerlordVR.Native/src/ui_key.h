/* =============================================================================
 * UI KEYING - the game's interface, over the stereo world, with the background
 * taken out.
 *
 * THE PROBLEM IT SOLVES
 *
 * Bannerlord draws its interface into its own swapchain, and the stereo path
 * never touches that image, so inside a mission none of the interface reached
 * the headset. Showing the whole image instead of the world reads the menu but
 * loses the battle, which is wrong for anything you are meant to use WHILE
 * playing - the tactics wheel above all, which exists to be used mid-fight and
 * is meaningless without the field behind it.
 *
 * What makes keying viable here is a quirk of how AFR gets the world in the
 * first place. VrAfrRenderer points the engine's own scene view at our eye
 * texture, so the game's swapchain during a mission holds the interface drawn
 * over NOTHING - no terrain, no soldiers, no sky. The background is not a busy
 * scene that a colour key would eat holes in; it is flat dark. That makes the
 * separation between "interface" and "not interface" close to exact, which is
 * the one condition under which keying is honest rather than a guess.
 *
 * So: read the game's image, keep the pixels that are lit, make the rest
 * transparent, and hand the result to the compositor as a quad it can blend
 * over the projection layer.
 * ========================================================================== */
#ifndef BVR_UI_KEY_H
#define BVR_UI_KEY_H

#include <d3d11.h>
#include <stdint.h>

namespace bvr {

/* Copies `src` into `dst` with alpha derived from brightness: dark stays
 * transparent, lit stays opaque, with a short ramp between so edges and
 * antialiased text do not crawl.
 *
 * `dst` must be the same size as `src` and carry a UAV-capable typeless format.
 * Returns false if anything was missing, in which case the caller should submit
 * no overlay rather than an unkeyed one - an unkeyed overlay is an opaque
 * rectangle across the middle of the world, which is worse than none.
 */
bool ui_key_apply(ID3D11Device* device, ID3D11DeviceContext* context,
                  ID3D11Texture2D* src, ID3D11Texture2D* dst);

/* Builds the monitor image: the eye the headset is showing, scaled to fit the
 * monitor with its aspect preserved, and the keyed interface composited on top.
 *
 * WHY THE MONITOR NEEDS REBUILDING AT ALL
 *
 * Because AFR took its picture away. VrAfrRenderer points the engine's own
 * scene view at our eye texture, so the game stops drawing the world into its
 * own swapchain and only the interface lands there - over whatever stale frame
 * was left behind. That is what the monitor was showing: a menu painted across
 * the last loading screen.
 *
 * So the mirror is assembled rather than captured. `eye` is the image the
 * headset is being handed, `ui` is the keyed interface from ui_key_apply, and
 * `dst` receives the two composited at the monitor's own size and aspect.
 *
 * Letterboxed rather than stretched: an eye image is a VR frustum, roughly
 * square, and squeezing that into 16:9 makes every face on the monitor wide and
 * wrong. Bars are honest about the shape difference.
 */
bool ui_mirror_compose(ID3D11Device* device, ID3D11DeviceContext* context,
                       ID3D11Texture2D* eye, ID3D11Texture2D* ui,
                       ID3D11Texture2D* dst);

/* Frees the shader and cached views. Safe to call twice. */
void ui_key_release();

} // namespace bvr

#endif /* BVR_UI_KEY_H */
