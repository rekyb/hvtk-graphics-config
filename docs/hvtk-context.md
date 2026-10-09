# hvtk-context — Heroes' Vow: Three Kingdoms Performance Context

> Source: converted verbatim from the original
> `legend_of_heros_unity_performance_context.json`. This is the original problem
> context for the low-effects work; the current approach and design live in
> `docs/superpowers/specs/2026-10-07-gfxconf-design.md`.

## Task

Modify a Unity game's assets to reduce unnecessary visual effects and improve
performance/stutter on low-performance devices (reference/test hardware:
Ryzen 5 5500U iGPU).

## Game

| Field | Value |
|---|---|
| Name | Heroes' Vow: Three Kingdoms |
| Install folder / exe | `LegendOfHeros` / `ThreeKingdom.exe` |
| Platform | Windows 11, Steam |
| Unity version | 2021.3.43f1c1 |
| Scripting backend | IL2CPP (inferred from GameAssembly.dll / log call stacks) |
| Graphics API | Direct3D 11.0, feature level 11.1 |
| GPU | AMD Radeon(TM) Graphics (Ryzen 5 5500U integrated GPU) |
| RAM | 16 GB |
| Resolution seen in log | 1600x900 |

## Original boot.config

| Key | Value |
|---|---|
| memorysetup-main-allocator-block-size | 33554432 |
| memorysetup-thread-allocator-block-size | 33554432 |
| memorysetup-gfx-main-allocator-block-size | 33554432 |
| memorysetup-gfx-thread-allocator-block-size | 16777216 |
| gfx-enable-gfx-jobs | 1 |
| gfx-enable-native-gfx-jobs | 1 |
| wait-for-native-debugger | 0 |
| hdr-display-enabled | 0 |
| gc-max-time-slice | 3 |

## Player log findings

### Graphics initialization

- GfxDevice reports `threaded=1` and `jobified=1`.
- Renderer is AMD Radeon(TM) Graphics.
- Game reports 1600x900 resolution.

### Important stutter event

Unity synchronously unloaded **17,017 unused assets**:

| Phase | Time (ms) |
|---|---|
| **Total** | **1033.3628** |
| find_live_objects | 13.8056 |
| create_object_mapping | 8.3506 |
| mark_objects | 982.1527 |
| delete_objects | 29.0533 |

**Interpretation:** at least one major ~1 second stutter is asset
cleanup/management, not purely GPU effects.

### Other warnings

- Repeated: `Look rotation viewing vector is zero`
- Repeated SceneGuard `PauseAllAnimators`/`ResumePausedAnimators`, sometimes involving 100+ animators
- Occasional: `DynamicCulling::no cameras assigned`
- Missing Behaviour scripts reported
- `Custom/LeafWindSwing_Final_Smooth_EdgeFix` fallback `Transparent/Unlit` not found
- Light Probe tetrahedron malformed warnings
- WindowsVideoMedia color-standard warnings

## Files examined or available

| File / source | Notes |
|---|---|
| globalgamemanagers | Uploaded / previously inspected |
| globalgamemanagers.assets | Uploaded / previously inspected |
| resources.assets | Uploaded; approximately 88 MB |
| GameValue.json | Gameplay/global constants, not useful graphics configuration |
| TKEditor documentation | Official data/content modding editor; no clear global graphics controls |
| Development tools PDF | Official development/mod workflow; no clear global graphics settings |

## Asset findings

**Important note:** presence of a shader/type does **NOT** prove that effect is
active. Locate active profiles/components and edit their serialized
settings/references rather than deleting shaders.

### Post-processing names seen or expected

PostProcessProfile, PostProcessEffectSettings, Bloom, AmbientOcclusion,
ScreenSpaceReflections, MotionBlur, DepthOfField, ChromaticAberration, Grain,
Vignette, ColorGrading, AutoExposure.

### Shader / system strings previously identified

- `Hidden/Post FX/Bloom`, `Hidden/Post FX/Motion Blur`, `Hidden/Post FX/Depth Of Field`
- `Hidden/Post FX/Ambient Occlusion`, `Hidden/Post FX/Screen Space Reflection`
- `Hidden/Post FX/Fog`, `Hidden/Post FX/Temporal Anti-aliasing`, `Hidden/Post FX/FXAA`
- `Hidden/PostProcessing/Bloom`, `Hidden/PostProcessing/DepthOfField`
- `Hidden/PostProcessing/MotionBlur`, `Hidden/PostProcessing/TemporalAntialiasing`
- `Hidden/PostProcessing/ScalableAO`, `Hidden/PostProcessing/MultiScaleVO`
- `Hidden/PostProcessing/ScreenSpaceReflections`
- `Hidden/Aura2/PostProcessShader`
- `URP VolumetricFog`

### Other rendering systems of interest

Aura2 volumetric lighting/fog · VolumetricFog / VolumetricFogProfile ·
DynamicFog / GlobalFog · GPU-instanced vegetation / GPUI · decals ·
vegetation/leaf wind shaders

## Desired low-effects configuration

### Disable first

| Effect | Disable |
|---|---|
| ScreenSpaceReflections | ✔ |
| AmbientOcclusion | ✔ |
| MotionBlur | ✔ |
| DepthOfField | ✔ |
| ChromaticAberration | ✔ |
| Aura2 / volumetric lighting | ✔ |
| VolumetricFog | ✔ |

### Optional second pass

- Bloom — disable if safe
- TemporalAA — disable; prefer FXAA or no AA
- MSAA — off/lowest if applicable
- ShadowDistance — reduce to roughly 25–40 if a global setting is found
- ShadowResolution — low/medium
- LODBias — roughly 0.6–0.8 if safely configurable

### Keep initially

- ColorGrading ✔
- Anisotropic filtering ✔
- TextureQuality — unchanged
- Vignette — can remain enabled; low priority

## Modification strategy (original asset-patch plan)

- **Preferred tool:** UABEA (Unity Asset Bundle Extractor Advanced) or another
  serializer that correctly supports Unity 2021.3 assets.
- **User has backups:** yes · **Willing to modify assets directly:** yes
- **Current state (at time of writing):** user can find PostProcessProfile in UABEA.

Instructions for the local agent:

1. Inspect `resources.assets` and relevant `sharedassets*.assets` using a proper Unity serializer.
2. Find actual PostProcessProfile instances and follow `m_Settings` PPtr references (`m_FileID`/`m_PathID`) to effect-setting MonoBehaviours.
3. Determine effect type from MonoScript/type tree rather than guessing from PathID.
4. Disable expensive effect settings/components using their serialized `enabled`/`active` fields.
5. Do not blindly set every MonoBehaviour `m_Enabled` to 0.
6. Do not delete/blank shader assets merely because their names match an effect.
7. Preserve object table structure, alignment, dependencies, and external references.
8. Write a new modified assets file rather than overwriting the only original.
9. Prefer a first test disabling SSR, AO, Motion Blur, DOF, Chromatic Aberration and volumetrics; optionally Bloom afterward.
10. Report exact PathIDs/types/fields changed so modifications are reproducible/reversible.

## Performance goal

- **Primary:** reduce GPU load and frame-time spikes on low-performance devices
  (reference hardware: Ryzen 5 5500U integrated graphics — the mod targets the
  device class, not this specific GPU).
- **Secondary:** reduce visual effects that are unnecessary to gameplay.
- **Caveat:** asset cleanup stutter (~1033 ms event) is a separate issue and may
  remain even after reducing post-processing.

## Original request to the local agent

Analyze the local Unity asset files directly and make a safe low-effects
variant. Use UABEA/UnityPy or equivalent tooling available locally. Identify
actual active PostProcessProfile and effect-setting objects, then disable the
expensive effects listed above. Do not guess byte offsets or destroy shaders.
Keep backups and produce a detailed change log with file, PathID, type, field,
old value, new value.
