# Enemy physics

Blade & Sorcery / Tales of Glory style melee for BannerlordVR: the blade
meets the bodies it touches instead of only triggering the game's attacks.
Code: `managed/BannerlordVR/VR/Physics/`, the ragdoll bridge
`native/BannerlordVR.Physics.Native/`, tests `tests/BannerlordVR.PhysicsTests/`.

## Turning it on

- **On by default** (`enemy_physics = 1`). `enemy_physics = 0` in
  `BannerlordVR.cfg` takes it out entirely: no physics patch is applied (each
  patch's `Prepare` returns false), its native DLL is not loaded, the panel
  box is hidden, and the game plays as without this code.
- On, the VR panel shows an **Enemy physics** box under Motion controls
  (`enemy_physics_active`, default 1). Unticked, the mod's own physical
  combat fights, with motion controls as they are. It needs the panel's
  motion controls on (`VrPhysicalCombat.Enabled`).
- On, the VR panel opens on **both stick clicks held 1 s**
  (`menu_chord_seconds`; 0 gives back both triggers held 2 s). With the
  physics, squeezing both hands is a grab, a fist or a neck cut held on an
  enemy. `End` still opens the panel.

## What it does

- Blades are tested against fitted body capsules of up to six nearby enemies
  (riders included); hits deal the game's damage through its own blows.
- **Stabs** (Tales of Glory's entry test: kinetic energy over a threshold set
  by armour, helmet, an open face and the saddle) stay in the body; a lethal
  one holds the victim at one point until the blade is drawn out
  (Blade & Sorcery). Riders and stabs from a horse riding past hold the
  blade on the part without a ragdoll.
- **Blade stops** and recoil on bodies, shields and enemy blades; axes bite
  and stay in; edges slice joints (dismemberment, beheading).
- **Ragdolls**: living enemies are pushed, staggered and knocked down; the
  killing blow throws the body, which stays physical for a few seconds.
- **Arrows** stuck in your body or shield can be pulled out, held and thrown.
- Grabs, chokes and neck cuts with the free hand; bleeding (off by default).

## Configuration keys

All under `enemy_physics = 1`. The tested setup is the defaults.

| Key | Default | |
|---|---|---|
| `enemy_physics` | 1 | master switch: 0 leaves the physics out entirely |
| `enemy_physics_active` | 1 | the panel's Enemy physics box |
| `enemy_physics_weapon_damage` | 1 | the physics deals blade damage on the bodies it tracks |
| `enemy_physics_living_ragdoll` | 1 | living ragdolls (needs `BannerlordVR.Physics.Native.dll`) |
| `enemy_physics_active_ragdoll` | 1 | balanced active ragdolls, stab joints, axe bites |
| `enemy_physics_neck_cut` | 1 | drawing the blade across a held neck |
| `enemy_physics_stab_model` | tog | `tog` (Tales of Glory) or `bs` (Blade & Sorcery penetration) |
| `enemy_physics_max_agents` | 6 | bodies simulated at once (adaptive) |
| `enemy_physics_mounted`, `enemy_physics_riders` | 1 | from the saddle, and against riders |
| `enemy_physics_bleeding`, `enemy_physics_wounds`, `enemy_physics_choke` | 0 | optional effects |
| `enemy_physics_sandbox` | 0 | training dummies and a duelist near the player |
| `enemy_physics_trace` | 0 | detailed log in `Logs\BannerlordVR.Physics.log` |
| `menu_chord_seconds` | 1 | panel chord; 0 restores the triggers |
| `save_load_guard` | 0 | the preload screen asks "load with different modules?" once |

## Hooks into existing code

Calls added to `SubModule.cs` (one line each, all return at once when
`enemy_physics` is 0): `VrEnemyPhysics.Load` (end of `OnSubModuleLoad`),
`ApplicationTick` (after `VrSystem.Tick`), `AfterAsyncTick` (new override),
`MissionBehaviorInitialize` (end of `OnMissionBehaviorInitialize`).

The panel box: `BvrOverlayState.enemyPhysics` appended last (788 bytes, the
size checks in `bvr_api.cpp` and `BannerlordVR.MathTests` updated), copied in
`bvr_api.cpp` and `vr_overlay.cpp` like `gripToggle`, drawn in
`vr_overlay.cpp` under Motion controls (hidden while managed sends -1), and
one line each in `VrMenu` (`Push`: `VrEnemyPhysics.PanelState`, `Poll`:
`VrEnemyPhysics.FromPanel`).

Everything else is a Harmony patch in `VR/Physics/`, applied by the mod's
own patch loop, so no existing file changes:

| Patch | Target | Why |
|---|---|---|
| `PhysicsTickPatch` | `MissionScreen.AfterMissionTick` (low priority, after `MissionTickPatch`) | the physics ticks after the hands |
| `ReleaseStrikePatch` | `VrPhysicalCombat.Strike` | one damage system per blade |
| `ProtectHeldVictimPatch` | `Agent.RegisterBlow` | no foreign blow on a held enemy |
| `TriggerOwnershipPatch` | `VrHolster.TriggerKeyAllowed` | a fist or a drawn cut owns the trigger |
| `MenuChord` | `VrMenu.TriggerHold` | panel chord |
| `PhysicalBlade.*` | `VrCollide.Resolve`, `VrCollide.Hostile`, `VrMelee.Fire`, `VrCombatSound.Swing` | the shown blade held by joints, shields, clashes and bites |
| `HitboxFit`, `HelmetVolume` | `VrPhysicalCombat.Fill` | fitted capsules, helmet volume |
| `ShieldPlate` | `VrBlade.TryGetEnemyPlate` | enemy shield plate fit |
| `HalfSword` | `VrTwoHand.Eligible` | one-handed blades can be half-sworded |
| `ThrownBlade` | `VrThrow.Fly`, `VrThrow.Tick` | thrown blades stick |
| `PlayerReach` | `VrRoomscale.Step`, `VrRoomscale.Tick` | close reach, stick lean |
| `OfficialPulsePatch` | `VrPhysicalCombat.Pulse` | haptics on both hands of a two-hander |
| `StuckMissiles.PlayerStickPatch` | `Mission.HandleMissileCollisionReaction` | arrows on the player and the shield |
| `BladeClash.BouncePatch` | `Mission.MeleeHitCallback` | enemy blades bounce off the player's |
| `DismemberFx.SeverKillsPatch` | `Mission.GetAgentState` | a severed agent dies |
| `SaveLoadGuard` | `PreloadScreen.OnFrameTick`, `SandBoxSaveHelper.TryLoadSave` | (own key) |

Read by reflection (`FastAccess`, `BladeShape`, `StuckMissiles`):
`VrBlade.TryGetOwnPlate`, `VrBlade.Bounds`, `VrDrawnBow.VertexCount`,
`VrTwoHand` (`Active`, `Gripping`), `VrSkinnedWeapon.TryGetVisual`,
`VrSkinnedWeapon.TryMeshWorld`, `VrPhysicalCombat.Capsule`.

`module/ModuleData/bvr_items.xml` adds one item, `bvr_severed_head` (a
meshless helmet a beheaded body wears), sold nowhere.

## Credits

The mechanics and tuning come from **Blade & Sorcery** (Warpfrog) and
**Tales of Glory** (BlackTale Games). No code or assets from either game are
included.
