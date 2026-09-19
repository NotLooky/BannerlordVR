using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Settings from a file, deliberately NOT from environment variables.
    ///
    /// A game launched by Steam or the TaleWorlds launcher inherits the
    /// environment those were started with, so a variable set in a shell never
    /// reaches it. Every switch built for isolating the Phase 4 crash was
    /// unreachable for that reason, which cost several test runs that proved
    /// nothing. A file has no such problem, and the native layer reads the same
    /// one, so both halves are configured in one place.
    ///
    ///     Documents\Mount and Blade II Bannerlord\Configs\BannerlordVR.cfg
    ///
    /// key=value per line; '#' or ';' starts a comment.
    /// </summary>
    public static class VrConfig
    {
        private static readonly Dictionary<string, string> Values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static bool _loaded;

        public static string Path { get; private set; }

        public static void Load()
        {
            if (_loaded)
                return;
            _loaded = true;

            try
            {
                Path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Mount and Blade II Bannerlord", "Configs", "BannerlordVR.cfg");

                if (!File.Exists(Path))
                {
                    VrLog.Info("No config at " + Path + "; using defaults.");
                    return;
                }

                foreach (string raw in File.ReadAllLines(Path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                        continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;

                    Values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }

                VrLog.Info($"Loaded {Values.Count} setting(s) from {Path}.");

                if (Values.TryGetValue("mode", out string mode))
                {
                    VrLog.Info($"Config mode = {mode.Trim().ToLowerInvariant()}; keys it "
                               + "supplies are defaults only, and anything written "
                               + "explicitly still wins.");
                }
            }
            catch (Exception ex)
            {
                VrLog.Error("Could not read the config; using defaults.", ex);
            }
        }

        // --- modes ------------------------------------------------------------
        //
        // Almost every key in this file is a detail. Four of them are not: together
        // they decide which of two architectures the mod is, and getting three of
        // the four right produces something worse than either.
        //
        //   mode = vp              the head pose is substituted into the engine's
        //                          view-projection matrix on its way to the GPU.
        //                          The engine's own camera never learns about the
        //                          head, so it culls, picks LODs and draws its sky
        //                          for wherever the mouse is pointing.
        //
        //   mode = engine-camera   the engine's own camera is moved to the eye and
        //                          given the headset's frustum, and nothing
        //                          downstream is touched at all.
        //
        // The second is the rung the BioShock Trilogy VR mod calls MonoTracked, and
        // its architecture notes are explicit that this is the one that pays: with
        // the engine's camera telling the truth, "the engine computes every
        // view-dependent effect natively", and the long tail of per-effect fixes
        // "mostly evaporates". Culling, sky, shadows and LOD stop being separate
        // problems because none of them was ever a separate problem - they were all
        // the same camera being wrong.
        //
        // A mode supplies DEFAULTS. Any key written explicitly still wins, so a
        // single line can still be pulled out of a mode for a comparison run.
        private static readonly Dictionary<string, string> EngineCameraMode =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "afr_camera_mode", "engine" },  // move CombatCamera itself
                { "afr_engine_fov",  "1" },       // and give it the headset frustum
                { "vp_patch",        "0" },       // nothing left downstream to fix
                // engine_aim is NOT in this bundle, though the reasoning above
                // says it should be: every tested config has overridden it back
                // to 1, so the bundle's 0 has never actually run. Leaving it here
                // would mean the no-config player is the only one getting it.
                { "symmetric_fov",   "1" },       // one frustum for both eyes
            };

        // Why symmetric belongs in this mode.
        //
        // Alternate-frame rendering draws ONE eye per frame through the engine's
        // single camera, so an asymmetric per-eye frustum means the projection
        // flips between a left-biased and a right-biased volume every frame.
        // Everything the engine keeps across frames that depends on the
        // projection - occlusion history, temporal filtering, streaming
        // decisions - is then being fed an alternating answer, which is the shape
        // of a problem we have already spent runs chasing under the name
        // "shaking".
        //
        // A symmetric frustum that CONTAINS the headset's asymmetric one is the
        // same choice the BioShock mod ships as its phase 1: "slight pixel waste,
        // correct output". Both eyes render through an identical, unchanging
        // projection and differ only by where the camera is, so they cannot fail
        // to line up. Both halves of the mod read this key and compute the
        // containing frustum the same way - max(|left|,|right|) and
        // max(|up|,|down|) - which for a mirrored pair is the same answer for
        // both eyes.
        //
        // symmetric_fov = 0 is now a correct configuration too rather than a
        // broken one (see HoldEngineFov), and costs no pixels. It is the sharper
        // setting, and the one to try once the image is trusted.

        /// <summary>Which architecture is selected. See the note above.</summary>
        public static string Mode
        {
            get
            {
                Load();
                // engine-camera, not vp, because a player who never opens the
                // config file must get the architecture this was tested on. The
                // note above says getting three of the four keys right is worse
                // than either mode; defaulting to the mode that was never played
                // is the same trap with no config file to blame it on.
                return Values.TryGetValue("mode", out string value) && value.Length > 0
                    ? value.Trim().ToLowerInvariant()
                    : "engine-camera";
            }
        }

        private static bool ModeDefault(string key, out string value)
        {
            value = null;
            return Mode == "engine-camera" && EngineCameraMode.TryGetValue(key, out value);
        }

        private static bool Lookup(string key, out string value)
        {
            Load();

            if (Values.TryGetValue(key, out value) && value.Length > 0)
                return true;

            return ModeDefault(key, out value);
        }

        /// <summary>
        /// Writes one setting back to the file, in place.
        ///
        /// This file is not a settings blob - it is the project's lab notebook, a
        /// thousand lines of test blocks recording why each value is what it is.
        /// So it is edited rather than rewritten: every UNCOMMENTED line assigning
        /// the key is replaced and everything else is preserved byte for byte. A
        /// key that does not appear is appended with a note saying who wrote it.
        ///
        /// Every uncommented occurrence, not just the first, because duplicates
        /// exist in this file and the LAST one is what both readers end up using.
        /// Rewriting only the first would leave the file saying one thing and the
        /// game doing another, which is the worst of the available outcomes.
        /// </summary>
        public static bool Save(string key, string value)
        {
            Load();
            Values[key] = value;

            if (string.IsNullOrEmpty(Path))
                return false;

            try
            {
                var lines = File.Exists(Path)
                    ? new List<string>(File.ReadAllLines(Path))
                    : new List<string>();

                bool replaced = false;

                for (int i = 0; i < lines.Count; i++)
                {
                    string trimmed = lines[i].TrimStart();
                    if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == ';')
                        continue;

                    int eq = trimmed.IndexOf('=');
                    if (eq <= 0)
                        continue;

                    if (!string.Equals(trimmed.Substring(0, eq).Trim(), key,
                                       StringComparison.OrdinalIgnoreCase))
                        continue;

                    lines[i] = key + " = " + value;
                    replaced = true;
                }

                if (!replaced)
                {
                    lines.Add(string.Empty);
                    lines.Add("# Written by the in-game VR menu.");
                    lines.Add(key + " = " + value);
                }

                File.WriteAllLines(Path, lines);
                return true;
            }
            catch (Exception ex)
            {
                VrLog.Error("Could not write " + key + " back to the config.", ex);
                return false;
            }
        }

        public static bool Bool(string key, bool fallback)
        {
            if (!Lookup(key, out string value))
                return fallback;

            value = value.Trim().ToLowerInvariant();
            return !(value == "0" || value == "false" || value == "off" || value == "no");
        }

        public static string String(string key, string fallback)
        {
            return Lookup(key, out string value) ? value : fallback;
        }

        public static float Float(string key, float fallback)
        {
            return Lookup(key, out string value) &&
                   float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? parsed
                : fallback;
        }

        public static int Int(string key, int fallback)
        {
            return Lookup(key, out string value) &&
                   int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : fallback;
        }
    }
}
