# Heroes' Vow: Three Kingdoms Graphics Config

A lightweight BepInEx mod for Heroes' Vow: Three Kingdoms that trims heavy visual effects to help the game run smoothly on lower-end hardware. It uses BepInEx 6 (IL2CPP) and a simple config file so you can toggle effects on the fly.

## Status

**Implemented — v0.2.0.** All spec deliverables built and test-verified; see
[`docs/superpowers/specs/`](docs/superpowers/specs/) for the approved design.

**→ Setup, config reference, overlay usage and troubleshooting:
[`docs/user-guide.md`](docs/user-guide.md).**

## Contents

| Path | Purpose |
|---|---|
| `docs/user-guide.md` | User guide: install/uninstall, all 18 config keys, F10 overlay, log troubleshooting |
| `docs/hvtk-context.md` | Original performance-analysis context (player log, asset findings, goals) |
| `docs/development-rules.md` | Rules all code must follow (BepInEx best practices + invariants) |
| `docs/project-config.md` | Toolchain, paths, metadata, build/deploy commands |
| `docs/superpowers/specs/` | Design spec |
| `src/` | Plugin source |
| `tools/` | Unity asset analysis scripts (UnityPy) |

## Features

- Per-effect toggles: PPv2 (AO, Chromatic Aberration, DOF, SSR, Motion Blur, Bloom), SCPE (Fog, CloudShadows, AO2D, Blur, Sharpen), volumetric fog, planar reflections, Aura2
- Optional antialiasing-mode override (FXAA/SMAA/TAA/None)
- In-game overlay (F10) to flip toggles live; config file remains source of truth
- Uninstall = delete `BepInEx/` + `winhttp.dll` from the game folder
