# hvtk-graphics-config

Config-driven low-effects mod for **LegendOfHeros / ThreeKingdom** (Unity 2021.3,
IL2CPP, Steam) targeting better performance on AMD Ryzen 5 5500U integrated
graphics.

Built on **BepInEx 6 (IL2CPP)** — all effects are toggled at runtime via a
config file; **no game asset files are modified**.

## Status

Design/spec phase — see [`docs/superpowers/specs/`](docs/superpowers/specs/).
Implementation not started yet.

## Contents

| Path | Purpose |
|---|---|
| `docs/hvtk-context.md` | Original performance-analysis context (player log, asset findings, goals) |
| `docs/superpowers/specs/` | Design spec |
| `src/` | Plugin source (planned) |
| `tools/` | Unity asset analysis scripts (UnityPy) |

## Planned features

- Per-effect toggles: PPv2 (AO, Chromatic Aberration, DOF, SSR, Motion Blur, Bloom), SCPE (Fog, CloudShadows, AO2D, Blur, Sharpen), volumetric fog, planar reflections, Aura2
- Optional antialiasing-mode override (FXAA/SMAA/TAA/None)
- In-game overlay (F10) to flip toggles live; config file remains source of truth
- Uninstall = delete `BepInEx/` + `winhttp.dll` from the game folder
