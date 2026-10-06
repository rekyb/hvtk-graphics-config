# Heroes' Vow: Three Kingdoms Graphics Config

A lightweight BepInEx mod for Heroes' Vow: Three Kingdoms that trims heavy visual effects to help the game run smoothly on lower-end hardware. It uses BepInEx 6 (IL2CPP) and a simple config file so you can toggle effects on the fly.

## Status

Design/spec phase — see [`docs/superpowers/specs/`](docs/superpowers/specs/).
Implementation not started yet.

## Contents

| Path | Purpose |
|---|---|
| `docs/hvtk-context.md` | Original performance-analysis context (player log, asset findings, goals) |
| `docs/development-rules.md` | Rules all code must follow (BepInEx best practices + invariants) |
| `docs/project-config.md` | Toolchain, paths, metadata, build/deploy commands |
| `docs/superpowers/specs/` | Design spec |
| `src/` | Plugin source (planned) |
| `tools/` | Unity asset analysis scripts (UnityPy) |

## Planned features

- Per-effect toggles: PPv2 (AO, Chromatic Aberration, DOF, SSR, Motion Blur, Bloom), SCPE (Fog, CloudShadows, AO2D, Blur, Sharpen), volumetric fog, planar reflections, Aura2
- Optional antialiasing-mode override (FXAA/SMAA/TAA/None)
- In-game overlay (F10) to flip toggles live; config file remains source of truth
- Uninstall = delete `BepInEx/` + `winhttp.dll` from the game folder
