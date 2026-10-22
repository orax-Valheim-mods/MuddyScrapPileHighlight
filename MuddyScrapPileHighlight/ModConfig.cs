using BepInEx.Configuration;
using UnityEngine;

namespace MuddyScrapPileHighlight
{
    /// <summary>
    /// Every tunable of the mod, bound to BepInEx/Config/orax.MuddyScrapPileHighlight.cfg.
    /// Entries are read live, so a change applies on the next tick without a restart.
    /// </summary>
    internal class ModConfig
    {
        internal readonly ConfigEntry<bool> Enabled;
        internal readonly ConfigEntry<KeyboardShortcut> ToggleKey;
        internal readonly ConfigEntry<bool> OnlyChunks;
        internal readonly ConfigEntry<float> MaxDistance;
        internal readonly ConfigEntry<float> ScanInterval;
        internal readonly ConfigEntry<bool> Shell;
        internal readonly ConfigEntry<Color> ShellColor;
        internal readonly ConfigEntry<float> ShellWidth;
        internal readonly ConfigEntry<bool> HideCryptWater;
        internal readonly ConfigEntry<bool> Marker;
        internal readonly ConfigEntry<Color> MarkerColor;
        internal readonly ConfigEntry<float> MarkerSize;
        internal readonly ConfigEntry<bool> MarkerDistance;
        internal readonly ConfigEntry<bool> DebugLog;

        internal ModConfig(ConfigFile file)
        {
            Enabled = file.Bind(
                "General",
                "Enabled",
                true,
                new ConfigDescription(
                    "Whether the highlight is on at startup. The toggle key switches it at runtime and writes this value."
                )
            );

            // Not bound out of the box: the highlight is simply on (or off, per Enabled)
            // until a key is chosen. KeyCode.None is honoured, PressedToggle ignores it.
            ToggleKey = file.Bind(
                "General",
                "ToggleKey",
                new KeyboardShortcut(KeyCode.None),
                new ConfigDescription(
                    "Key that toggles the highlight on and off. Not bound by default - "
                        + "set a chord to enable the toggle."
                )
            );

            // An intact pile is a Destructible; the frac chunk that replaces it after one
            // pickaxe blow (a MineRock5 of ~30 areas) is the real target, and it only ever
            // exists while a pile is being mined.
            OnlyChunks = file.Bind(
                "General",
                "OnlyChunks",
                true,
                new ConfigDescription(
                    "Highlight only the fracturable chunks (mudpile_frac / mudpile2_frac) - "
                        + "the fragments a pickaxe blow leaves behind - and leave intact "
                        + "piles dark. Off, an intact pile is highlighted instead whenever "
                        + "no chunk exists in range."
                )
            );

            MaxDistance = file.Bind(
                "General",
                "MaxDistance",
                5f,
                new ConfigDescription(
                    "How far from the player piles are tracked and highlighted, in metres. Piles beyond it lose their shell.",
                    new AcceptableValueRange<float>(1f, 10f)
                )
            );

            ScanInterval = file.Bind(
                "General",
                "ScanInterval",
                1f,
                new ConfigDescription(
                    "Seconds between two scans of the loaded zones for piles.",
                    new AcceptableValueRange<float>(0.2f, 10f)
                )
            );

            Shell = file.Bind(
                "Highlight",
                "Shell",
                true,
                new ConfigDescription("Draw the glowing shell over the piles.")
            );

            // Orange: the swamps are brown-green and the crypts are pitch black, so a warm
            // colour reads best there. The alpha doubles as the shell opacity.
            ShellColor = file.Bind(
                "Highlight",
                "ShellColor",
                (Color)new Color32(255, 191, 0, 16),
                new ConfigDescription("Shell colour, alpha included.")
            );

            ShellWidth = file.Bind(
                "Highlight",
                "ShellWidth",
                0.03f,
                new ConfigDescription(
                    "How far the shell floats over the pile surface, in metres.",
                    new AcceptableValueRange<float>(0.005f, 0.2f)
                )
            );

            // The crypt water is drawn by Custom/Water, which hardcodes its depth-write, so
            // the shell cannot be composited over it. Hiding that one body of water is the
            // only way to see shells under it. Only crypt water is touched: the ocean and
            // the swamp surface keep theirs.
            HideCryptWater = file.Bind(
                "Highlight",
                "HideCryptWater",
                false,
                new ConfigDescription(
                    "Hide the green water of Sunken Crypts so the shells show through it. "
                        + "Off, the crypt water is drawn as usual and hides underwater "
                        + "shells (its shader cannot be drawn over)."
                )
            );

            // Off by default: the shell alone reads fine up close, and a marker over
            // everything is a strong visual that not every player wants on by default.
            Marker = file.Bind(
                "Highlight",
                "Marker",
                false,
                new ConfigDescription(
                    "Draw an on-screen marker over every tracked fragment, on top of "
                        + "everything: walls, terrain and darkness cannot hide it. Fragments "
                        + "outside the view get an arrow at the screen edge instead."
                )
            );

            // Same orange as the shell by default, but fully opaque: a marker has no
            // surface to blend with.
            MarkerColor = file.Bind(
                "Highlight",
                "MarkerColor",
                (Color)new Color32(255, 140, 0, 255),
                new ConfigDescription("Marker and distance colour, alpha included.")
            );

            MarkerSize = file.Bind(
                "Highlight",
                "MarkerSize",
                12f,
                new ConfigDescription(
                    "Marker size in pixels.",
                    new AcceptableValueRange<float>(4f, 40f)
                )
            );

            MarkerDistance = file.Bind(
                "Highlight",
                "MarkerDistance",
                true,
                new ConfigDescription("Show the distance to the fragment next to its marker.")
            );

            DebugLog = file.Bind(
                "General",
                "DebugLog",
                false,
                new ConfigDescription("Log every scan and every pile found, with its position.")
            );
        }
    }
}
