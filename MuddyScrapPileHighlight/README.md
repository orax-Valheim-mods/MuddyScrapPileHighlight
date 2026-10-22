# MuddyScrapPileHighlight

Swamp iron is hard to see: Muddy Scrap Piles sit in dark water, behind crypt walls, at
the bottom of murky pits. This mod makes every fragment a pickaxe blow leaves behind
glow, and puts a marker on each one, so you can tell at a glance how much iron is left
in a pile and where the last pieces are.

- **Glowing shell** over every remaining fragment of a Muddy Scrap Pile. The glow
  follows the pile as it shrinks: mined fragments stop glowing one by one.
- **One on-screen marker per fragment**, with its distance, drawn on top of everything
  - terrain, walls and darkness cannot hide it. A fragment behind you or off-screen
  gets an arrow at the screen edge pointing to it.
- **Fragments only.** Intact piles stay dark; the highlight appears on the first
  pickaxe blow and tracks the chunks from then on. (Set `OnlyChunks = false` to
  highlight intact piles too, whenever no fragment is in range.)
- **Toggle with F8**, fully configurable: distance, colors, marker size, shell width.
- **Client-side only.** Nothing is spawned or sent over the network: other players and
  the server do not need the mod.
- **Active only inside Sunken Crypts.** Everywhere else - open swamp, base, other
  biomes - the mod idles completely, so it costs nothing to carry around. The crypt is
  where the piles worth highlighting are. (Muddy Scrap Piles that also spawn *buried in
  the open swamp* are deliberately **not** highlighted, as that keeps the mod idle
  outside crypts.)

# Installation

This mod is client-side only. You do not need to install it on the server.

## Using a mod manager (recommended)

You can install the mod using Vortex, R2ModMan or Gale.

## Manual

Install BepInExPack for Valheim.
Copy the DLL file to `Steam\steamapps\common\Valheim\BepInEx\plugins\`

# Configuration

`BepInEx\config\orax.MuddyScrapPileHighlight.cfg` - every entry is read live, so a
change applies at once, without a restart.

> **Scope:** the highlight only runs while the player is inside a **Sunken Crypt**.
> Outside one, no piles are tracked, no shell or marker is drawn and nothing is scanned.
> This is fixed behaviour (not a config entry) so the mod costs nothing anywhere but a
> crypt; the buried outdoor piles of the swamp are therefore not highlighted.

| Entry | Default | What it does |
| --- | --- | --- |
| `Enabled` | `true` | Highlight on at startup. The toggle key writes this value. |
| `ToggleKey` | `None` | Key that toggles the highlight on and off. Not bound by default. |
| `OnlyChunks` | `true` | Highlight only the fragments a pickaxe leaves behind. Off, an intact pile is highlighted when no fragment is in range. |
| `MaxDistance` | `5` | How far from the player piles are tracked, in metres. |
| `ScanInterval` | `1` | Seconds between two scans of the loaded zones. |
| `Shell` | `true` | Draw the glowing shell. |
| `ShellColor` | `#FFBF0010` | Shell colour, alpha included (RRGGBBAA). |
| `ShellWidth` | `0.03` | How far the shell floats over the pile surface, in metres. |
| `HideCryptWater` | `false` | Hide the green water of Sunken Crypts so shells show through it (its shader cannot be drawn over). Only crypt water is affected. |
| `Marker` | `false` | Draw the on-screen marker per fragment. |
| `MarkerColor` | orange | Marker and distance colour, alpha included. |
| `MarkerSize` | `12` | Marker size in pixels. |
| `MarkerDistance` | `true` | Show the distance next to each marker. |
| `DebugLog` | `false` | Log scans, tracked piles and diagnostics. |

# Requirements

- [BepInExPack for Valheim](https://thunderstore.io/package/denikson/BepInExPack_Valheim/)
- [Jotunn](https://thunderstore.io/package/ValheimModding/Jotunn/)

# Misc.

Source code: [GitHub repository](https://github.com/orax-Valheim-mods/)
