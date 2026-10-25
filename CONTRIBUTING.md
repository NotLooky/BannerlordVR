# Contributing

Base your branch on the tag `v0.2.7-alpha.1-src`. That commit builds the same
`BannerlordVR.dll` as the v0.2.7-alpha.1 release. Build steps are in
[docs/BUILDING.md](docs/BUILDING.md). `dotnet build` copies the mod into your
game folder; add `-p:SkipDeploy=true` when you don't want that.

The main branch keeps moving while your pull request is open, so a few rules
keep both sides working after the merge:

1. **New behaviour goes behind a config key that defaults to off**, for example
   `VrConfig.Bool("my_feature", false)`. With the key off, the game must play
   exactly as it does without your change.
2. **Add, don't rename.** Don't rename, remove, or change the parameters of
   existing members in `VrCollide`, `VrPhysicalCombat`, `VrMelee`, `VrBlade`,
   `VrMenu` or other existing classes. Add new members or new files instead.
   If an existing method has to change, keep the edit small and explain it in
   the pull request.
3. **Prefer new files.** Put most of a feature in its own `VrSomething.cs` and
   keep the hooks into existing files to a few lines.
4. **One feature per pull request.** In the description, list each place your
   code hooks into existing code and every config key you added.
5. **Test both ways** before opening the pull request: with your keys off
   (nothing changed) and on. Say which headset and runtime you used.
