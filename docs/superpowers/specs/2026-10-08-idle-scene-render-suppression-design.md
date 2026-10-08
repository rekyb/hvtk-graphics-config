# GFXConf — Idle-Scene 3D Render Suppression

Date: 2026-10-08
Status: draft for user review
Scope path: architectural addition to the existing GFXConf plugin

This design builds on
`docs/superpowers/specs/2026-10-07-gfxconf-design.md`. It does not change
the existing visual-effect sweep or modify game assets.

## 1. Goal and evidence

Stop rendering 3D in confirmed idle scenes that cause severe frame-rate drops,
while keeping the last 3D view visible and all screen-space UI live. The game
uses additive scenes, so matching and exclusions operate on the full loaded
scene set, not only Unity's active scene.

The existing throwaway probe measured culling the cameras to about 54 FPS from
roughly 5–8 FPS in farmland. A follow-up probe captured a 1600x900 frame,
displayed it with screen-space canvases restored, and the user confirmed the
UI continued updating and responding. FPS from the snapshot spike was variable
and is not treated as a clean performance benchmark.

## 2. Scope and non-goals

### In scope

- Allowlist-driven suppression of 3D in idle scenes.
- A frozen 3D snapshot behind live screen-space UI.
- Automatic apply, refresh, and restore as additive scenes load and unload.
- A configurable manual capture hotkey that adds a scene to the allowlist.
- Allowlist-only scene matching; the veto set starts empty.

### Out of scope

- Modifying or replacing game assets.
- FPS/frametime logging in the released plugin.
- Adding interactive battle, war, duel, or debate scenes to the default
  allowlist.
- Pausing `Time.timeScale` as a substitute for reducing render submission.
- Modifying or disabling the game's `EventSystem`.
- Harmony patches.
- The known asset-cleanup stutter.

## 3. Configuration contract

Add these entries to the existing BepInEx config file, under `[Scenes]`:

```ini
[Scenes]
SceneSuppressionAllowlist = SS_Farmland, SS_City_Market, SS_City_Street
CaptureSceneHotkey = F9
```

- `SceneSuppressionAllowlist` is a comma-separated list of exact runtime scene
  names. The default contains only the three scenes the user has tested.
- Users edit the allowlist in `gfxconf.cfg`; text editing is not added to the
  F10 overlay. Direct config edits take effect on the next launch.
- The parser trims whitespace, ignores empty items, and compares scene names
  case-insensitively. Duplicate entries are treated as one entry.
- `CaptureSceneHotkey` is a configurable Unity key, default `F9`.
- Pressing the capture hotkey appends the most recently loaded scene
  to the allowlist and saves `gfxconf.cfg`. It does not add duplicates. A
  successful addition is applied immediately if the current loaded set is
  eligible.
- Use F9 only in a scene confirmed safe to suppress. Adding a scene makes it
  eligible immediately if it is currently loaded.

## 4. Scene eligibility and vetoes

Seed the loaded-scene set on plugin startup, then maintain it from scene load
and unload notifications, tracking scene handles as well as names so additive
instances can be removed correctly. Track the most recently loaded scene from
`sceneLoaded` for the capture hotkey. Do not use
`SceneManager.GetActiveScene()` as the match source or capture target.

Suppression is eligible exactly when at least one loaded scene name matches
`SceneSuppressionAllowlist`. The veto set is empty initially; do not add a
scene veto unless the user later identifies that interactive scene.

If the user later reports an interactive scene that must be protected, add its
exact runtime name to a code-owned veto set using the probe's
`SCENE LOADED ... loaded set ...` log. Do not infer runtime names from asset
filenames. Until such a veto is added, an interactive scene loaded alongside
an allowlisted scene will also have its 3D suppressed. This is a known risk.

The known safe defaults are `SS_Farmland`, `SS_City_Market`, and
`SS_City_Street`. The observed additive helper/parent scenes are not added to
the allowlist just to make active-scene matching work; the presence of a
configured target in the loaded set is sufficient.

## 5. Capture, display, and lifecycle

### Snapshot and live UI

When suppression first becomes eligible:

1. Temporarily disable enabled `ScreenSpaceOverlay` canvases for one rendered
   frame so the screenshot contains the 3D view but not a frozen copy of live
   screen UI.
2. Capture the frame with `ScreenCapture.CaptureScreenshotAsTexture()`.
3. Restore each canvas to its prior enabled state.
4. Display the texture using a non-raycast `RawImage` on a screen-space canvas
   ordered behind the game's existing screen-space canvases.
5. Set every camera's `cullingMask` to zero and disable each
   `PostProcessLayer`, while keeping the cameras enabled.

The brief capture frame may omit screen-space UI. The UI remains live and
interactive after capture. World-space canvases are part of the frozen 3D
image. No game input system is modified.

### Transitions

- On scene load or unload, recompute eligibility from the full loaded set.
- Evaluate the already-loaded set once after config binding during plugin
  startup; do not wait for another scene event to apply an eligible default.
- On entry to an eligible set, capture before suppressing rendering.
- If the set of loaded allowlisted scenes changes while still eligible,
  restore rendering for the capture frame, refresh the image, then suppress
  rendering again.
- If no allowlisted scene remains, restore each saved camera mask and
  post-processing enabled state, then destroy the snapshot texture and display
  object.
- Camera references are not retained across scene-set changes after restore.
- Scene suppression does not depend on `ReapplyOnSceneLoad` or `DelaySeconds`;
  it follows the loaded-scene eligibility rule directly.

### Failure behavior

- Any capture/setup failure leaves or restores normal 3D rendering; it must
  never leave cameras culled with no valid snapshot.
- A failed scene-load or scene-unload subscription disables suppression for
  the session, because reliable restore could not be guaranteed.
- Every touch of game state is wrapped in try/catch and logs a warning; no
  exception may escape into Unity callbacks.
- If a transition fails while suppression is active, best-effort restore all
  saved camera and post-processing state, destroy the snapshot, and fail open.
- No camera is disabled, and no Harmony patch is used.

## 6. Logging

Log only state transitions and manual capture outcomes; no per-frame output.
Logs must make it possible to verify:

- Which target scene made suppression eligible.
- How many cameras and post-processing layers were changed.
- When suppression was restored and why (target left or failure).
- Which exact scene the capture hotkey added, or why capture was skipped.
- Whether snapshot capture failed and normal rendering was retained.

Do not add FPS/frametime logging to GFXConf. Performance verification remains a
manual test using the throwaway probe.

## 7. Files and implementation boundaries

- `src/GFXConf/Config.cs`: bind the allowlist and capture hotkey entries.
- `src/GFXConf/Plugin.cs`: subscribe to scene loaded and unloaded events and
  pass transitions to the suppression owner.
- `src/GFXConf/Overlay.cs`: keep scene-suppression state and snapshot handling
  in the existing `GfxBehaviour`; handle the capture hotkey here. Keep the
  F10 overlay draggable; do not add an allowlist text editor.
- `src/GFXConf/GFXConf.csproj`: add only the required existing game interop
  references for screen capture and UI texture display.
- `docs/user-guide.md`: document defaults, manual scene capture, config-file
  editing, click-through behavior, and the locked-scene veto limitation.
- This spec and the base config schema §4.2 must remain consistent.

No game DLLs, game assets, or NuGet dependencies are added or modified.

## 8. Test plan

1. **Build/deploy:** Release build succeeds and only deploys
   `GFXConf.dll` to `BepInEx/plugins/`.
2. **Config defaults:** a fresh config contains the three tested scene names
   and `CaptureSceneHotkey = F9`; an empty allowlist touches no rendering state.
3. **Loaded-set matching:** farmland, market, and town match by loaded scene
   name even when the active scene is the shared parent.
4. **Allowlist-only matching:** an allowlisted scene triggers suppression even
   when an unlisted scene is also loaded. A veto scene is not recognized until
   the user reports its exact runtime name and a code-owned veto is added.
5. **Snapshot/UI:** snapshot shows the last 3D view; screen-space progress,
   buttons, and popups remain live and responsive; no `EventSystem` changes or
   input-block logs occur.
6. **Refresh/restore:** switching between eligible additive scene sets
   refreshes the snapshot. Leaving the target set or loading a veto restores
   the exact prior camera masks and post-processing states and releases the
   snapshot resources.
7. **Manual capture:** F9 appends the latest loaded scene, persists it, does
   not duplicate it, and applies immediately when eligible. The user must
   only use F9 in a scene confirmed safe to suppress.
8. **Fail-open:** force snapshot capture/setup failure; verify normal 3D
   rendering remains or is restored and the game continues without an
   unhandled exception.
9. **Regression:** exercise the existing visual-effect sweep and draggable
   F10 overlay; verify no new `GFXConf`/Unity NREs. Expected
   `GUILayout.Window unavailable` fallback warning remains unrelated.
10. **Performance:** use the throwaway probe in the three defaults and any
    added test scene; report sweep/log evidence and separate transition
    stutters from steady-state samples. Do not add runtime FPS logging.

## 9. Known limits

- The veto set is initially empty. Until the user identifies an interactive
  scene and its exact runtime name is added as a veto, allowlisting another
  loaded scene can suppress it too.
- F9 is configurable in case it conflicts with another game binding.
- A one-frame screen-space UI blink can occur when a snapshot is captured or
  refreshed.
- The frozen texture consumes memory proportional to screen resolution and is
  destroyed when suppression ends.
