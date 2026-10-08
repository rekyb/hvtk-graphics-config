# GFXConf — Low-Effects Config Plugin for Heroes' Vow: Three Kingdoms

Date: 2026-10-07
Status: approved by user 2026-10-07 (implementation may proceed)
Scope path: architectural (new project — BepInEx 6 IL2CPP plugin)

## 1. Goal

Reduce GPU load and frame-time spikes on low-performance devices (reference
hardware: AMD Ryzen 5 5500U iGPU) by disabling
expensive visual effects **at runtime**, controlled by an easy-to-edit config
file, without modifying any game asset file.

Primary success criteria:

- Expensive effects (AO, volumetrics, planar reflections, SCPE passes) provably
  disabled, with per-scene counts logged as evidence.
- Every effect group individually toggleable via config; changes applyable
  without reinstalling or re-patching anything.
- Game boots and plays normally with the plugin installed; removing the plugin
  folder fully restores stock behavior.

Known non-goal: the ~1033 ms Unity asset-cleanup stutter documented in
`docs/hvtk-context.md` is a separate issue and is not addressed by this plugin.

## 2. Environment (verified facts)

- Game: Heroes' Vow: Three Kingdoms — `C:\Program Files (x86)\Steam\steamapps\common\LegendOfHeros\ThreeKingdom.exe`
- Unity 2021.3.43f1c1, IL2CPP, D3D11, metadata version 31
- BepInEx 6.0.0-be.788 (Unity IL2CPP, win-x64) installed in game folder;
  interop generated successfully (127 assemblies in `BepInEx/interop/`)
- .NET SDK 10.0.401 installed; plugin targets `net6.0`
- Type locations in interop:
  - PPv2 (`PostProcessProfile`, `PostProcessLayer`, effect settings): `Unity.Postprocessing.Runtime.dll`
  - SCPE effects: `sc.posteffects.runtime.dll`
  - `VolumetricFog` (VolumetricFogAndMist): `Assembly-CSharp.dll` / `Assembly-CSharp-firstpass.dll`
  - Ceto `PlanarReflection`: `Assembly-CSharp-firstpass.dll`
  - Aura2: `Aura2_Core.dll`

## 3. Target inventory (from full asset scan; counts = instances found)

| Group | Class | Count | Action |
|---|---|---|---|
| PPv2 | AmbientOcclusion | 16 (resources 10, sa4 1, sa13 1, sa73 4) | disable setting |
| PPv2 | ChromaticAberration | 15 (resources 11, sa73 4) | disable setting |
| PPv2 | DepthOfField | 2 | disable setting |
| PPv2 | ScreenSpaceReflections | 0 (future-proof) | disable setting |
| PPv2 | MotionBlur | 0 (future-proof) | disable setting |
| PPv2 | Bloom | 21 (resources 15, sa4 1, sa13 1, sa73 4) | config toggle, default **enabled (not disabled)** |
| SCPE | Fog | 18 (resources 12, sa4 1, sa13 1, sa73 4) | disable setting |
| SCPE | CloudShadows | 15 (resources 12, sa73 3) | disable setting |
| SCPE | AmbientOcclusion2D | 9 (resources) | disable setting |
| SCPE | Blur | 4 (resources) | disable setting |
| SCPE | Sharpen | 4 (sa73) | disable setting |
| Component | VolumetricFogAndMist.VolumetricFog | 2 (resources) + 4 (level73) | `enabled = false` |
| Component | Ceto.PlanarReflection | 1 (level1) + 1 (level2) | `enabled = false` |
| Component | Aura2 (Aura/AuraVolume/AuraCamera) | 0 instances (future-proof) | `enabled = false` |
| Layer | PostProcessLayer.antialiasingMode | present in level4-14,73 | optional override |

Kept untouched: ColorGrading, Vignette, Grain, SCPE Gradient/Danger,
DynamicFog profiles, all profile assets, `m_Enabled` of unrelated components.

## 4. Architecture

Single BepInEx BasePlugin (`GFXConf`) in `src/GFXConf/`, deployed as one DLL to
`<game>/BepInEx/plugins/GFXConf.dll`. Config file (BepInEx `ConfigFile`, TOML)
at `<game>/BepInEx/config/gfxconf.cfg` — source of truth. In-game F10 overlay
(IMGUI) edits the same ConfigFile entries and saves.

### 4.1 Data flow

1. `Awake()`: bind config entries, log banner, run initial sweep after
   `DelaySeconds` (default 2 s).
2. Subscribe to `UnityEngine.SceneManagement.SceneManager.sceneLoaded`
   (IL2CPP-safe delegate) → schedule sweep for that scene after `DelaySeconds`.
3. Sweep (single method, try/catch around each group):
   - **Settings groups:** `Resources.FindObjectsOfTypeAll<PostProcessProfile>()`
     (catches shared + volume-instantiated copies). For each `profile.settings`
     entry, match concrete type name against enabled toggles → set
     `active = false`; `enabled.value = false`; `enabled.OverrideState = true`.
   - **Component groups:** `FindObjectsOfType` for `VolumetricFog`,
     `PlanarReflection` (Ceto), Aura2 `Aura`/`AuraVolume`/`AuraCamera`
     (type lookups by name across loaded assemblies; missing type = skip)
     → `behaviour.enabled = false`.
   - **AA override:** if `OverrideMode != KeepOriginal` → for each
     `PostProcessLayer`, parse config string to enum by name and assign
     `antialiasingMode`.
   - Log one summary line per sweep:
     `"[GFXConf] scene=73: AO=4, CA=4, SCPE.Fog=4, VolumetricFog=4, planar=0"`.
4. F10 overlay: controls the existing interactive settings; on change →
   `ConfigFile.Save()` + immediate sweep (effects flip live). Scene allowlist
   and capture-hotkey entries are config-file-only, not F10 text controls.

### 4.2 Config schema (defaults)

```ini
[PPv2]
DisableAmbientOcclusion = true
DisableChromaticAberration = true
DisableDepthOfField = true
DisableScreenSpaceReflections = true
DisableMotionBlur = true
DisableBloom = false

[SCPE]
DisableFog = true
DisableCloudShadows = true
DisableAmbientOcclusion2D = true
DisableBlur = true
DisableSharpen = true

[Volumetrics]
DisableVolumetricFog = true
DisablePlanarReflections = true
DisableAura2 = true

[Antialiasing]
OverrideMode = KeepOriginal   ; None | FastFXAA | FXAA | SMAA | TAA

[General]
ReapplyOnSceneLoad = true
DelaySeconds = 2
EnableF10Overlay = true

[Scenes]
SceneSuppressionAllowlist = SS_Farmland, SS_City_Market, SS_City_Street
CaptureSceneHotkey = F9
```

### 4.3 Error handling

- Each sweep group in its own try/catch → `Log.Warning`, never rethrow
  (chainloader survival is a hard requirement; game is known to crash).
- Missing types (e.g., Aura2 stripped from a build) → log once, skip silently.
- Null profile/settings entries → skipped.
- Config parse failures fall back to BepInEx defaults.

## 5. Build & deploy

- `src/GFXConf/GFXConf.csproj`: SDK-style, `net6.0`, `EnableDynamicLoading`,
  references BepInEx core DLLs from `<game>/BepInEx/core/` and interop DLLs
  from `<game>/BepInEx/interop/` (HintPaths; game folder is read dependency,
  never written by build output except plugins dir).
- Build: `dotnet build -c Release`; post-build copy DLL to `<game>/BepInEx/plugins/`.
- Rebuild loop ≈ seconds; no game asset files ever touched.

## 6. Testing plan

1. **Boot smoke test**: launch game → menu; assert log contains banner +
   summary line, no `Error`/`Unhandled exception` in `LogOutput.log`.
2. **Scene coverage**: enter gameplay (level73 load) → assert summary counts
   match scan expectations (AO, CA, SCPE, VolumetricFog > 0).
3. **Toggle proof**: flip `DisableBloom = true`, restart → Bloom count appears
   in summary; visual difference confirms effect.
4. **AA override**: set `OverrideMode = FXAA` → summary logs layer changes;
   visual shimmer check optional.
5. **F10 overlay**: toggle a group live → summary on next sweep reflects it.
6. **Uninstall**: rename `BepInEx` + `winhttp.dll` → game boots stock
   (Steam integrity untouched either way).
7. **Crash safety**: force an exception path (temp debug flag) → game continues,
   warning logged.

## 7. Deliverables

- `src/GFXConf/` — plugin source (csproj + Plugin.cs + Overlay.cs)
- `docs/superpowers/specs/2026-10-07-gfxconf-design.md` — this spec
- `docs/superpowers/plans/…` — implementation plan (next step)
- Installed artifact: `GFXConf.dll` + generated `gfxconf.cfg` in game folder
- `docs/user-guide.md` — install/uninstall/config reference for the user

## 8. Explicitly out of scope

- Asset byte-patching path (design kept in `tools/` scripts; can be revisited)
- Frame-time/FPS logging
- Fixing the 1033 ms asset-cleanup stutter
- Steam cloud / achievements interaction (BepInEx adds only new files; Steam
  verify ignores extras)
