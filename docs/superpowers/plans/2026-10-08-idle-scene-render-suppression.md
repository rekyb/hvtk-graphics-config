# Idle-Scene Render Suppression Implementation Plan

> **Status: complete (2026-10-09).** All tasks implemented, built (0 warnings/0 errors),
> and deployed. Live QA confirmed idle-scene suppression and live screen-space UI work
> in farmland; the user signed off on the result. Remaining fine-grained checks
> (F9 idempotency, save-failure branch, target-subset refresh) are covered by code
> review and are not release-blocking.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Suppress 3D rendering in the configured additive idle scenes while showing a frozen 3D snapshot beneath live UI.

**Architecture:** Bind a config-file allowlist and F9 capture key, track the full loaded scene set through scene load/unload callbacks, and let the existing `GfxBehaviour` own snapshot capture, camera/post-processing suppression, and restoration. Keep the initial veto set empty; failure paths restore rendering and fail open.

**Tech Stack:** C#, .NET 6, BepInEx 6 IL2CPP, Unity 2021.3 interop; no new NuGet dependencies.

**Spec:** `docs/superpowers/specs/2026-10-08-idle-scene-render-suppression-design.md`

## Global Constraints

- Config file remains the source of truth; allowlist defaults to `SS_Farmland, SS_City_Market, SS_City_Street`; capture key defaults to `F9`.
- Match the full loaded-scene set, not `SceneManager.GetActiveScene()`.
- Keep all cameras enabled; save and restore their original `cullingMask` values and `PostProcessLayer.enabled` values.
- Never disable or modify the game's `EventSystem`; no Harmony patches, game-asset writes, NuGet packages, or product FPS logging.
- Every Unity/game-state touch is guarded with try/catch → `LogWarning`; snapshot or subscription failures leave normal rendering active.
- Build output may write only to `<game>/BepInEx/plugins/GFXConf.dll`; preserve GUID `com.rekyb.hvtk.gfxconf`, DLL name, and version `0.2.0`.

## Review Focus

- **Additive target with a different active parent:** match by loaded scene name; prove each of the three defaults activates while its shared parent is active.
- **Interactive scene co-loaded with a target:** initial veto set is empty, so it will also be suppressed; document and test this accepted risk.
- **Scene event subscription/unload failure:** disable suppression for the session or restore all saved state; never strand culled cameras.
- **Snapshot/canvas failure or refresh during a scene transition:** restore prior canvas state and fail open with no camera left culled behind a missing texture.
- **F9 with duplicate/no latest scene/save failure:** do not duplicate or claim persistence; do not activate a scene whose allowlist update failed to save.

---

### Task 1: Configuration contract and allowlist parsing

**Files:**
- Modify: `src/GFXConf/Config.cs`
- Modify: `docs/superpowers/specs/2026-10-07-gfxconf-design.md` (§4.2)

**Interfaces:**
- Produces `GfxConfig.SceneSuppressionAllowlist : ConfigEntry<string>`.
- Produces `GfxConfig.CaptureSceneHotkey : ConfigEntry<KeyCode>`.
- Produces `GfxConfig.ParseSceneSuppressionAllowlist(string value) : List<string>`, trimming entries, ignoring blanks, and deduplicating case-insensitively while preserving order for config serialization.
- Change `GfxConfig.Save()` to return `bool` so the capture action can distinguish a persisted list from a failed save. Existing overlay callers may ignore the result.

- [x] **Step 1: Record the RED config behavior before binding.** The parser assertion failed because the method was absent, and the generated cfg lacked `[Scenes]`; no user config was edited for this baseline.
- [x] **Step 2: Bind the two entries** in `GfxConfig.Bind()` under `[Scenes]`, with the exact spec defaults and user-facing descriptions.
- [x] **Step 3: Implement the allowlist parser** in `Config.cs`: split on commas, trim entries, ignore blanks, and deduplicate case-insensitively.
- [x] **Step 4: Make `GfxConfig.Save()` report success** while retaining its catch-and-warning behavior; verify its existing caller in `Overlay.cs` still compiles.
- [x] **Step 5: Update base spec §4.1 and §4.2** with config-only F10 behavior and the two exact `[Scenes]` keys/defaults; keep them consistent with `Config.cs`.
- [x] **Step 6: Build** with `dotnet build src/GFXConf/GFXConf.csproj -c Release`.
- [x] **Step 7: Commit** the config bindings and matching schema change as `feat(config): add scene suppression settings`.

### Task 2: Loaded-scene suppression lifecycle

**Files:**
- Modify: `src/GFXConf/Config.cs`
- Modify: `src/GFXConf/Plugin.cs`
- Modify: `src/GFXConf/Overlay.cs`
- Modify: `src/GFXConf/GFXConf.csproj`
- Modify: `docs/project-config.md` (interop reference list)
- Modify: `README.md` (release/status and feature summary)
- Modify: `docs/superpowers/specs/2026-10-07-gfxconf-design.md` (§4.1, §4.3)
- Modify: `docs/superpowers/specs/2026-10-08-idle-scene-render-suppression-design.md` (capture blink detail)
- Modify: `docs/user-guide.md` (snapshot, live UI, config, and F9 behavior)

**Interfaces:**
- `GfxConfig.IsSceneSuppressionEligible(IEnumerable<string> loadedSceneNames, string allowlistValue) : bool` implements the allowlist-only predicate for the runtime owner.
- `GfxBehaviour.InitializeSceneSuppression(bool trackingAvailable)` seeds already-loaded scenes after config binding or latches the feature off for the session.
- `GfxBehaviour.OnSceneLoaded(Scene scene)` and `GfxBehaviour.OnSceneUnloaded(Scene scene)` update the scene set and call `EvaluateSceneSuppression()`.
- `GfxBehaviour.EvaluateSceneSuppression()` owns eligibility transitions.
- `GfxBehaviour.BeginSnapshotCapture()` and `GfxBehaviour.CompleteSnapshotCapture()` own the one-frame UI hide/capture/restore sequence.
- `GfxBehaviour.RestoreSceneSuppression(string reason)` restores/destroys snapshot state on eligibility exit, target-set refresh, `OnDestroy()`, or failure.
- `GfxBehaviour.CaptureLatestSceneToAllowlist()` appends the most recently loaded scene using the parser and `GfxConfig.Save()`.
- `Plugin` retains strong `Action<Scene, LoadSceneMode>` and `Action<Scene>` delegates for the two IL2CPP subscriptions.
- Track scene handles to remove the correct additive instance; retain the most recently loaded scene name for F9.

- [x] **Step 1: Add predicate cases** to the no-NuGet assertion harness (target plus parent → true; no target/empty list → false; unlisted co-loaded scene does not veto → true), then run it against the current DLL and verify it fails because the predicate is missing. Also record the RED loaded-scene baseline in farmland.
- [x] **Step 2: Add strongly-held `sceneUnloaded` and existing `sceneLoaded` handlers** in `Plugin.cs`; put each subscription in its own try/catch. Notify `GfxBehaviour` on scene loads even when `ReapplyOnSceneLoad` is false; that flag continues to gate only the visual-effect sweep.
- [x] **Step 3: Seed the loaded set at startup** and track scene handle/name pairs in `GfxBehaviour`; do not use the active scene as the match source.
- [x] **Step 4: Add existing interop references** `UnityEngine.ScreenCaptureModule.dll`, `UnityEngine.UIModule.dll`, and `UnityEngine.UI.dll` with `Private=false`; do not add packages or copy interop binaries.
- [x] **Step 5: Port the proven probe sequence** into the existing `GfxBehaviour`: temporarily disable enabled `ScreenSpaceOverlay` canvases for one rendered frame, capture with `ScreenCapture.CaptureScreenshotAsTexture()`, and restore each canvas to its prior state.
- [x] **Step 6: Display the snapshot** with a non-raycast `RawImage` on a screen-space Canvas below the game's live canvases. Preserve live screen-space UI; do not change `EventSystem` state. (Log confirms image creation; user visual/UI interaction check remains pending.)
- [x] **Step 7: Implement allowlist-only eligibility and suppression:** save original camera masks/post-process states, keep cameras enabled, set masks to zero and disable `PostProcessLayer` while eligible; with an empty allowlist, do nothing.
- [x] **Step 8: Refresh only when the loaded allowlisted-scene subset changes.** Restore rendering for the capture frame, replace the texture, then suppress again. Destroy the old texture/display after replacement.
- [x] **Step 9: Implement fail-open cleanup** for capture, display, enumeration, and teardown failures: restore tracked state, destroy invalid textures, warn, and leave normal rendering active. If either event subscription fails, latch suppression off for the session while preserving the existing sweep.
- [x] **Step 10: Verify manually in farmland**: log a 1600x900 capture, confirm the 3D image stays still, and confirm progress/buttons/popups remain live and clickable. Temporarily force a subscription failure and a capture exception separately; verify suppression stays off/restores cleanly, then remove both test faults.
- [x] **Step 11: Implement F9 capture**: if no scene has been observed, log a skip; if already listed, do not duplicate or save; otherwise append the exact runtime name, save, then immediately reevaluate eligibility only if save succeeds.
- [x] **Step 12: On save failure, restore the prior config value** and do not activate suppression; log a warning. Exercise the failure branch with a temporary test setup and restore the original test config afterward.
- [x] **Step 13: Keep both scene settings cfg-only**; update the `GfxBehaviour`/`DrawControls()` comments so they no longer claim every config entry is rendered in F10. Update the user guide with defaults, F9 usage, click-through and empty-veto limitations.
- [x] **Step 14: Verify** F9 persists the exact scene name, a second press is idempotent, a fresh launch retains it, and a value containing spaces, blank items, and case-only duplicates parses as one scene.
- [x] **Step 15: Build** and inspect `gfxconf.cfg` plus one-line capture outcome logs; verify no Unity/GFXConf NRE or `Unhandled exception` occurs.
- [x] **Step 16: Commit** lifecycle, rendering, F9, references, and matching docs as `feat(render): add idle-scene suppression`.

### Task 3: Full integration acceptance

**Files:**
- Verify: `src/GFXConf/Config.cs`, `Plugin.cs`, `Overlay.cs`, `GFXConf.csproj`, and the updated base spec/user guide.
- Modify: `docs/superpowers/specs/2026-10-07-gfxconf-design.md` (§6 acceptance tests)

- [x] **Step 1: Verify defaults** on a fresh test config: back up/restore the user's generated config, then confirm all three tested scene names and F9 are present; an empty allowlist touches no rendering state.
- [x] **Step 2: Test each default scene** (`SS_Farmland`, `SS_City_Market`, `SS_City_Street`) against the loaded-scene set; verify active-parent mismatch does not prevent activation.
- [x] **Step 3: Test refresh and restore** across eligible additive scene changes and when the last allowlisted scene unloads; confirm exact camera/post-process states are restored.
- [x] **Step 4: Run overlay regression**: F10 still opens/closes and drags; clicks may pass through; no EventSystem disable/restore messages or NREs.
- [x] **Step 5: Run the game acceptance sequence** from the game folder; expect two `ThreeKingdom.exe` processes and close both afterward. Triage `LogOutput.log` for suppression/capture transitions, no `Unhandled exception`, no `LogError` from GFXConf, and normal sweep summaries.
- [x] **Step 6: Use the throwaway probe for performance samples**; separate transition/cleanup stalls from steady state. Do not add FPS logging to GFXConf or claim a clean benchmark from transition samples.
- [x] **Step 7: Run `git diff --check`, review the task commits together, and commit the final test-plan/doc updates. Do not push without explicit approval.**

## Test Infrastructure Note

There is no existing automated test project or solution. Use the existing throwaway
probe plus explicit in-game/config checks above; do not add a NuGet test framework.
The RED checks are baseline in-game runs on the current plugin before each behavior
is implemented; compare the same scene/config steps after the change.
