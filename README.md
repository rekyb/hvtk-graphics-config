# Heroes' Vow: Three Kingdoms Graphics Config

A lightweight BepInEx mod for Heroes' Vow: Three Kingdoms that trims heavy visual effects to help the game run smoothly on lower-end hardware. It uses BepInEx 6 (IL2CPP) and a simple config file so you can toggle effects on the fly.

## Status

**Released — v0.2.0.** Effect toggles, F10 overlay, and allowlisted idle-scene
render suppression are implemented and test-verified; see
[`docs/superpowers/specs/`](docs/superpowers/specs/) for the approved designs.

**→ Setup, config reference, overlay usage and troubleshooting:
[`docs/user-guide.md`](docs/user-guide.md).**

## Contents

| Path | Purpose |
|---|---|
| `docs/user-guide.md` | User guide: install/uninstall, all 20 config keys, F10 overlay, log troubleshooting |
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
- Idle-scene render suppression: freezes a 3D snapshot and stops drawing scene
  cameras in configured idle scenes (`SS_Farmland`, `SS_City_Market`,
  `SS_City_Street`), keeping screen-space UI live; F9 captures a new scene
- Uninstall = delete `BepInEx/` + `winhttp.dll` from the game folder
