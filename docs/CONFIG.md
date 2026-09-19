# Configuration

One file, read by both halves of the mod at startup:

```
Documents\Mount and Blade II Bannerlord\Configs\BannerlordVR.cfg
```

Every line is `key = value`. `#` or `;` starts a comment. Edit it, save it, and
restart the game — launch it however you normally do, Steam included.

**Most people never need to open this file.** The in-VR panel (`End`) covers
what you actually want to change while playing, and writes it back here as soon
as you change it.

> **A warning worth taking seriously.** The config file exposes nearly every
> internal switch in the mod, including the ones an experiment was hung on for
> an afternoon. **If a key is not listed on this page, treat it as development
> machinery rather than a setting** — that goes double for anything named
> `*_probe`, `*_census`, `*_measure` or `*_debug`. Some combinations are known
> to be broken with each other. If you change one and the mod misbehaves,
> restore the `BannerlordVR.cfg` that came with the release before reporting
> anything.

---

## The ones worth touching

### Comfort and scale

| Key | What it does |
|---|---|
| `world_scale` | How big the world feels. Also on the panel. |
| `render_scale` | Per-eye render resolution multiplier. Also on the panel. Raising it above the value you launched with waits for a relaunch. |
| `sharpen` | 0–1 sharpening pass. Also on the panel. |
| `hide_body` | Hide the player's own body, which is otherwise in your face. |
| `head_move` | Head movement drives the viewpoint. |
| `head_move_mounted` | The same while mounted. Off by default; the engine seats riders differently. |

### Aiming

| Key | What it does |
|---|---|
| `head_aim` | Aim where you look. |
| `head_aim_pitch` | Include pitch, not just yaw. |
| `head_aim_gain` | How hard the aim chases your head. Raise it if aim lags a fast head turn; lower it if the crosshair visibly hunts around the target. |
| `crosshair`, `crosshair_distance`, `crosshair_size_px` | The VR crosshair. It is drawn where the head actually points, which is why it exists at all. |

### Controllers

| Key | What it does |
|---|---|
| `gamepad` | Controllers act as an Xbox pad. |
| `gamepad_deadzone` | Stick deadzone. |
| `stick_mouse`, `stick_mouse_speed`, `stick_mouse_stick` | Drive the mouse cursor from a stick, for menus. Exclusive with real mouse input — touch the mouse and it hands back. |
| `motion_hands` | Experimental motion-control hands and melee. The gamepad emulation keeps working either way. |
| `grip_align_key`, `grip_calibrate_key`, `grip_default_key` | Rebind the calibration keys (`F11`, `Home`, `Insert`). |

### Screens and UI

| Key | What it does |
|---|---|
| `flat_screen`, `flat_screen_width`, `flat_screen_distance` | The flat panel that menus and the campaign map are shown on. Turning it off puts the headset on the stereo path everywhere, which leaves menus blank. |
| `ui_show`, `ui_overlay_width`, `ui_overlay_distance` | The in-mission UI overlay, drawn on the view rather than in the room. |
| `monitor_mirror` | Fix up the desktop window's picture. `0` leaves it as the engine left it, which looks broken. |

### Rendering

| Key | What it does |
|---|---|
| `stereo_mode` | `afr` (the shipping path, cheapest) or `nativestereo` (both eyes rendered for real, much more expensive). Also on the panel; a change takes effect at the next mission. |

---

## Rebuilding a clean config

If your config gets into a state you cannot explain, delete it and copy the
`BannerlordVR.cfg` from the release zip back in. That file is generated from the
exact configuration the release was tested on, with every duplicate resolved —
it is not the same file as `config/BannerlordVR.cfg` in this repository, which is
a development journal and is not meant to be played on.
