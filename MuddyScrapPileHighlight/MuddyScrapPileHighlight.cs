using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace MuddyScrapPileHighlight
{
    /// <summary>
    /// Highlights every loaded Muddy Scrap Pile close to the player.
    ///
    /// Purely client side: nothing is spawned, registered or sent over the network, so
    /// other players and servers do not need the mod. See PileTracker for how piles are
    /// found and ShellHighlighter for how they are drawn.
    /// </summary>
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    internal class MuddyScrapPileHighlight : BaseUnityPlugin
    {
        public const string PluginGUID = "orax.MuddyScrapPileHighlight";
        public const string PluginName = "MuddyScrapPileHighlight";
        public const string PluginVersion = "0.1.0";

        // Use this class to add your own localization to the game
        // https://valheim-modding.github.io/Jotunn/tutorials/localization.html
        public static CustomLocalization Localization =
            LocalizationManager.Instance.GetLocalization();

        private ModConfig config;
        private PileTracker tracker;
        private ShellHighlighter shell;
        private ScreenMarkers markers;

        /// <summary>
        /// When the highlight is next allowed to refresh. The scan and the shells only
        /// have to catch a chunk appearing or a piece being mined, both of which happen
        /// over whole seconds, so they are gated onto ScanInterval instead of running
        /// every frame - all that stays per frame is the toggle, because a key press is
        /// only visible on the frame it happens.
        /// </summary>
        private float nextSyncTime;

        /// <summary>
        /// The mod only ever runs inside a Sunken Crypt: that is where the muddy scrap
        /// piles worth highlighting are, and staying idle everywhere else costs nothing.
        /// Refreshed on a timer from the game's own dungeon flag (see IsInSunkenCrypt),
        /// so there is nothing to pay for in the per-frame path.
        /// </summary>
        private bool inCrypt;

        private float nextCryptCheck;

        /// <summary>
        /// How often the crypt test re-runs. Stepping in or out of a crypt is a slow, rare
        /// event, so a few seconds of slack is free: the mod lights up or dims out at most
        /// this long after crossing the threshold. Leaving the world stays instant, since
        /// the player-null guard in the active test never waits for this timer.
        /// </summary>
        private const float CryptCheckInterval = 3f;

        /// <summary>
        /// The environment Valheim forces while the local player stands inside a Sunken
        /// Crypt (set by the crypt's EnvZone trigger, which also drives its ambience).
        /// </summary>
        private const string CryptEnvName = "SunkenCrypt";

        private void Awake()
        {
            config = new ModConfig(Config);
            tracker = new PileTracker(config, Logger);
            shell = new ShellHighlighter(config, Logger);
            markers = new ScreenMarkers(config, Logger);

            Logger.LogInfo(
                $"{PluginName} {PluginVersion} loaded - toggle: {DescribeShortcut(config.ToggleKey.Value)}, "
                    + $"chunks: mudpile_frac / mudpile2_frac, distance: {config.MaxDistance.Value:0} m, "
                    + $"shell: {config.ShellColor.Value}"
            );
        }

        /// <summary>
        /// Whether the previous frame was in the working state (enabled and in a world).
        /// The cleanup only has to run on the way out of it, not on every frame spent
        /// there - while inactive nothing repopulates the tracker or the shells, so one
        /// pass is enough and the frames in between cost nothing but a bool compare.
        /// </summary>
        private bool wasActive;

        private void Update()
        {
            if (PressedToggle())
            {
                config.Enabled.Value = !config.Enabled.Value;
                Logger.LogInfo(
                    config.Enabled.Value
                        ? "MuddyScrapPileHighlight enabled"
                        : "MuddyScrapPileHighlight disabled"
                );
            }

            // Whether the player stands in a Sunken Crypt, refreshed on a timer: that is
            // the only place the mod works, so it stays idle everywhere else. The check
            // reads one field the game already maintains, so it has no business in the
            // per-frame path. In the menu no crypt is forced, so it reads false as is.
            if (Time.time >= nextCryptCheck)
            {
                nextCryptCheck = Time.time + CryptCheckInterval;
                inCrypt = IsInSunkenCrypt();
            }

            // The player guard also keeps the shell material from being probed in the
            // main menu, where the game's shaders are not all loaded yet. The crypt gate
            // keeps it idle outside a Sunken Crypt. Kept ahead of the sync timer so a
            // toggle, a return to the world or a step back into the crypt acts on the very
            // next frame instead of waiting for the next sync.
            bool active = config.Enabled.Value && Player.m_localPlayer != null && inCrypt;
            if (!active)
            {
                if (wasActive)
                {
                    // Just dropped out of the working state (toggled off, left the world,
                    // or stepped out of the crypt): clean up once, then every following
                    // inactive frame is free. shell.Clear also restores any crypt water
                    // this mod had hidden, so leaving puts the dungeon back as it was.
                    tracker.Reset();
                    shell.Clear();
                    nextSyncTime = 0f;
                    wasActive = false;
                }

                return;
            }

            wasActive = true;

            // One time compare per frame; the real work below only runs on the
            // ScanInterval cadence. The clamp matches the one inside PileTracker.Tick so
            // both fire on the very same frame.
            if (Time.time < nextSyncTime)
            {
                return;
            }

            nextSyncTime = Time.time + Mathf.Max(0.2f, config.ScanInterval.Value);

            // The scan tick does the expensive scene traversal on its own interval and
            // only reports "changed" then; between ticks ShellHighlighter does nothing
            // but a few config compares and one timer.
            bool changed = tracker.Tick(Time.time);
            shell.Sync(tracker.Chunks, changed);
        }

        private void OnGUI()
        {
            if (!config.Enabled.Value || Player.m_localPlayer == null || !inCrypt)
            {
                return;
            }

            // Drawn last so the marker sits over the game's own GUI as well.
            markers.Draw(tracker.Chunks);
        }

        private void OnDestroy()
        {
            // Script reload or shutdown: the shells would otherwise outlive the plugin.
            shell?.Clear();
        }

        /// <summary>
        /// True when the local player stands inside a Sunken Crypt. Valheim marks a
        /// dungeon's interior with an EnvZone trigger volume: while the player is inside,
        /// EnvZone.OnTriggerStay forces the "SunkenCrypt" environment (EnvMan.m_forceEnv),
        /// and the moment they step out, EnvZone.OnTriggerExit clears it again - a world
        /// load clears it too (Player.SetLocalPlayer). So that flag reads exactly "the
        /// player is inside the crypt volume". It is a volume test, unlike the old 3 m
        /// collider probe, which also fired next to the entrance shaft and the surface
        /// stone outside and left the mod on after the dungeon was left behind.
        /// </summary>
        private bool IsInSunkenCrypt()
        {
            EnvMan env = EnvMan.instance;
            return env != null
                && string.Equals(
                    env.m_forceEnv,
                    CryptEnvName,
                    System.StringComparison.OrdinalIgnoreCase
                );
        }

        /// <summary>
        /// A press of the configured chord, ignoring it while the console, a menu, the
        /// chat or a corpse have the input.
        /// </summary>
        private bool PressedToggle()
        {
            KeyboardShortcut shortcut = config.ToggleKey.Value;
            if (shortcut.MainKey == KeyCode.None || InputBlocked())
            {
                return false;
            }

            if (!Input.GetKeyDown(shortcut.MainKey))
            {
                return false;
            }

            foreach (KeyCode modifier in shortcut.Modifiers)
            {
                if (!Input.GetKey(modifier))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool InputBlocked()
        {
            if (Console.IsVisible() || Menu.IsVisible())
            {
                return true;
            }

            if (Chat.instance != null && Chat.instance.HasFocus())
            {
                return true;
            }

            Player player = Player.m_localPlayer;
            return player != null && player.IsDead();
        }

        private static string DescribeShortcut(KeyboardShortcut shortcut)
        {
            if (shortcut.MainKey == KeyCode.None)
            {
                return "not set";
            }

            List<string> keys = new List<string>();
            foreach (KeyCode modifier in shortcut.Modifiers)
            {
                keys.Add(modifier.ToString());
            }

            keys.Add(shortcut.MainKey.ToString());
            return string.Join("+", keys);
        }
    }
}
