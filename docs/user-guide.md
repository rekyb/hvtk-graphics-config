# GFXConf — User Guide

Config-driven low-effects plugin for *Heroes' Vow: Three Kingdoms*.
Version **0.2.0** (plugin ID `com.rekyb.hvtk.gfxconf`, DLL `GFXConf.dll`).

GFXConf disables expensive visual effects at runtime. It **never modifies any
game asset file** — everything it does lives in `BepInEx\` (plugin, config,
log) plus the BepInEx loader files next to the game exe.

## 1. Install

1. Install **BepInEx 6** (Unity IL2CPP, win-x64 build) into the game folder
   `...\steamapps\common\LegendOfHeros\`. This creates `BepInEx\` and
   `winhttp.dll` next to `ThreeKingdom.exe`. On first launch BepInEx generates
   its interop assemblies (`BepInEx\interop\`) — this is normal and only
   happens once.
2. Copy `GFXConf.dll` into `BepInEx\plugins\`.
3. Launch the game. On the first launch GFXConf creates its config file
   `BepInEx\config\gfxconf.cfg` with all defaults.

That's it — no reinstall or re-patching is ever needed to change settings;
edit the config (or use the in-game overlay, see §3).

## 2. Uninstall

Delete `BepInEx\` **and** `winhttp.dll` from the game folder. The game then
boots exactly like a stock install (Steam integrity is untouched either way —
BepInEx only ever adds new files, Steam Verify ignores extras).

> Do **not** delete anything inside `ThreeKingdom_Data\` — GFXConf never
> touches it, and those are real game files.

## 3. In-game overlay (F10)

Press **F10** in game to open the GFXConf overlay: every config key as a
live control. The overlay does not block game input, so clicks may reach
game UI behind it. Press **F10** again to close. The overlay can be
**dragged by its title bar**; it snaps back to the top-left corner every
time it is reopened.

- **Settings toggles are LIVE.** Flipping any toggle writes the value to
  `gfxconf.cfg` immediately (saved to disk) **and** triggers an immediate
  effect sweep — PPv2/SCPE settings flip on the spot.
- **Component toggles take effect on the NEXT LAUNCH.** `DisableVolumetricFog`,
  `DisablePlanarReflections` and `DisableAura2` are disable-only while the
  game runs: restarting the game re-enables those components, and the toggle
  state in `gfxconf.cfg` decides at the next launch whether they are disabled
  again.
- **`EnableF10Overlay = false` means the overlay cannot open.** F10 is still
  able to *close* an overlay that is already open — the gate only blocks
  opening. To turn the overlay back on, set `EnableF10Overlay = true` in
  `gfxconf.cfg` and restart (or re-check the toggle inside the open overlay
  before closing it).
- **`OverrideMode` in the overlay is a cycle button:**
  `KeepOriginal → None → FastFXAA → FXAA → SMAA → TAA → wrap`.

## 4. Config reference — `BepInEx\config\gfxconf.cfg`

All 18 keys, their sections, defaults and descriptions (spec §4.2
word-for-word). The file is TOML-style; edit it with the game **closed** (or
via the overlay) and restart for component-level changes.

### `[PPv2]` — Post Processing Stack v2 effects

| Key | Default | Description |
|---|---|---|
| `DisableAmbientOcclusion` | `true` | Disable the PPv2 Ambient Occlusion effect. |
| `DisableChromaticAberration` | `true` | Disable the PPv2 Chromatic Aberration effect. |
| `DisableDepthOfField` | `true` | Disable the PPv2 Depth of Field effect. |
| `DisableScreenSpaceReflections` | `true` | Disable the PPv2 Screen Space Reflections effect. |
| `DisableMotionBlur` | `true` | Disable the PPv2 Motion Blur effect. |
| `DisableBloom` | `false` | Disable the PPv2 Bloom effect (default false = keep Bloom). |

### `[SCPE]` — Scene Color Processing Effects

| Key | Default | Description |
|---|---|---|
| `DisableFog` | `true` | Disable the SCPE Fog effect. |
| `DisableCloudShadows` | `true` | Disable the SCPE Cloud Shadows effect. |
| `DisableAmbientOcclusion2D` | `true` | Disable the SCPE Ambient Occlusion 2D effect. |
| `DisableBlur` | `true` | Disable the SCPE Blur effect. |
| `DisableSharpen` | `true` | Disable the SCPE Sharpen effect. |

### `[Volumetrics]` — component-based effects

| Key | Default | Description |
|---|---|---|
| `DisableVolumetricFog` | `true` | Disable VolumetricFog components (volumetric fog and mist). |
| `DisablePlanarReflections` | `true` | Disable Ceto PlanarReflection components. |
| `DisableAura2` | `true` | Disable Aura 2 components (Aura, AuraVolume, AuraCamera). |

### `[Antialiasing]`

| Key | Default | Description |
|---|---|---|
| `OverrideMode` | `KeepOriginal` | PostProcessLayer antialiasing override: KeepOriginal (default) or `None \| FastFXAA \| FXAA \| SMAA \| TAA`. **Alias mapping:** `FXAA` and `FastFXAA` both use this build's single FastApproximateAntialiasing mode — `FastFXAA` sets `fastMode = true`, `FXAA` sets `fastMode = false`; `None`/`SMAA`/`TAA` set only the mode. Anything else logs one warning and leaves the game's original AA untouched. |

### `[General]`

| Key | Default | Description |
|---|---|---|
| `ReapplyOnSceneLoad` | `true` | Re-run the effect sweep after each scene load. |
| `DelaySeconds` | `2` | Seconds to wait after a scene load before the sweep runs. |
| `EnableF10Overlay` | `true` | Enable the F10 in-game settings overlay. |

## 5. Troubleshooting — reading the log

The log is **`<game>\BepInEx\LogOutput.log`**, i.e.
`C:\Program Files (x86)\Steam\steamapps\common\LegendOfHeros\BepInEx\LogOutput.log`.
Close the game (or the BepInEx console) before copying it — it is locked
while the game runs.

Also check Unity's own player log:
`C:\Users\<you>\AppData\LocalLow\FreeWing\ThreeKingdom\Player.log`.
Unity **engine** errors (e.g. `FindAllObjectsOfType`) appear THERE, not in
`BepInEx\LogOutput.log` — a clean session should show none of them.

Filter for `GFXConf|Unhandled exception`. A **clean run** looks like this
(pinned log line formats):

```
[Info   :   BepInEx] Loading [GFXConf 0.2.0]
[Info   :   GFXConf] [GFXConf] v0.2.0 loaded (com.rekyb.hvtk.gfxconf)
[Info   :   GFXConf] [GFXConf] config: <game>\BepInEx\config\gfxconf.cfg
[Info   :   GFXConf] [GFXConf] scene=StartMenu sweep in 2s
[Info   :   GFXConf] [GFXConf] scene=StartMenu: AO=0, CA=0, DoF=0, SSR=0, MB=0, Bloom=0, SCPE.Fog=0, SCPE.CloudShadows=0, SCPE.AO2D=0, SCPE.Blur=0, SCPE.Sharpen=0, VolumetricFog=0, planar=0, aura=0, aa=0
```

What to look for:

- `[GFXConf] v0.2.0 loaded ...` — plugin loaded (banner appears once).
- `scene=<name> sweep in <n>s` — a sweep was scheduled (on startup, on each
  scene load, or from an overlay toggle).
- **One** `scene=<name>: AO=...` summary line per completed sweep, ending in
  `aa=<n>`. (At the main menu all counts are 0 — effects load with gameplay
  levels.)
- **Zero** `Unhandled exception` lines. If the game misbehaves and you see
  none of the `[GFXConf]` lines at all, the plugin isn't loading — check that
  `GFXConf.dll` is in `BepInEx\plugins\`.
Known benign lines (not errors):

- `[Warning:   GFXConf] [GFXConf] GUILayout.Window unavailable — overlay uses fixed
  panel: ...` — logged once the first time the overlay opens; the overlay
  falls back to its fixed panel and works normally.
- `[Warning:Il2CppInterop] Class::Init signatures have been exhausted, using a
  substitute!` — emitted by BepInEx/Il2CppInterop itself, unrelated to GFXConf.

Warnings you may trigger **on purpose** (each is one line, the game keeps
running, one summary still follows):

- `invalid OverrideMode: <value>` — typo'd `OverrideMode` in the config.
- `type not found: <name>` — a component type missing from the build (logged
  once per session, group skipped).

If a sweep itself fails you get exactly one
`sweep failed (scene=...)` / `settings sweep failed (...)` warning instead of
a crash — the plugin catches every group separately and never rethrows.

**Heavy scenes are baseline performance, not config-fixable:** a heavy scene
(e.g. farmland) can run at ~4–5 FPS **with or without** the plugin (verified
by A/B). The effect toggles don't remove scene geometry/foliage cost — that
is the game's baseline performance in that scene, not something the config
can change.
