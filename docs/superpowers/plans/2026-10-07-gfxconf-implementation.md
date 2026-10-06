# GFXConf Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the config-driven BepInEx 6 IL2CPP plugin that disables expensive visual effects at runtime per spec, with an F10 overlay, never touching game assets.

**Architecture:** One `BasePlugin` (`GFXConf`) whose `Load()` binds a custom `ConfigFile` (`gfxconf.cfg`), registers an IL2CPP-injected `MonoBehaviour` (F10 input + sweep scheduler pump), and subscribes to `sceneLoaded`. Sweeps run on schedule only: find PPv2/SCPE settings via `Resources.FindObjectsOfTypeAll<PostProcessProfile>()`, components via name-based type lookup, then disable per config with one summary log line per sweep.

**Tech Stack:** C# / net6.0 (SDK 10.0.401), BepInEx 6.0.0-be.788 Unity IL2CPP, game interop assemblies, Unity IMGUI. No NuGet packages.

**Spec:** `docs/superpowers/specs/2026-10-07-gfxconf-design.md` (approved 2026-10-07 — this plan argues from it; executors read both)

## Global Constraints

- Identity: GUID `com.rekyb.hvtk.gfxconf`, plugin name `GFXConf`, version `0.1.0`, DLL `GFXConf.dll` (spec §4, AGENTS.md rule 6).
- Target `net6.0`, `EnableDynamicLoading=true`; references only from `<game>/BepInEx/core/` and `<game>/BepInEx/interop/` via HintPath with `Private=false`. **No NuGet packages.**
- The only write into the game folder is `<game>/BepInEx/plugins/` (build output) plus BepInEx-generated `config/` and log files. Reads only under `ThreeKingdom_Data\`.
- Config file `<game>/BepInEx/config/gfxconf.cfg`; all 18 entries bound word-for-word from spec §4.2 (keys, defaults, section names). `DelaySeconds` is `ConfigEntry<int>` (default 2); `OverrideMode` is `ConfigEntry<string>` (default `KeepOriginal`); `DisableBloom` default `false`.
- Zero Harmony patches. Every sweep group wrapped in its own try/catch → `LogWarning`, never rethrow. Missing type → log once, skip.
- Settings touched **only** if their concrete type name is in `SettingToggles` — ColorGrading, Vignette, Grain, SCPE Gradient/Danger, DynamicFog are never touched (spec §3).
- Log prefix `[GFXConf]`. Summary line format pinned in Task 3's Interfaces block.
- BepInEx 6 IL2CPP entry point is `BasePlugin.Load()` (spec §4.1's "Awake()" is the same hook, Mono naming).
- Tests are runtime game tests (no test framework — NuGet ban): evidence = build output + `LogOutput.log` lines. Launch = **two** `ThreeKingdom.exe` processes; kill after every test.
- **Every commit is local.** Present message + files + diff summary to the user; push only on explicit approval (AGENTS.md rule 7).

## Standard Test Cycle (referenced by every task as Cycle T1–T4)

```powershell
# T1 build (PostBuild copies DLL to plugins)
dotnet build "C:\Users\rekyb\Desktop\test-mod\src\GFXConf\GFXConf.csproj" -c Release
# expect: "Build succeeded", output shows GFXConf.dll copied to <game>\BepInEx\plugins\

# T2 launch from game dir (expect TWO ThreeKingdom.exe processes)
Start-Process -FilePath "C:\Program Files (x86)\Steam\steamapps\common\LegendOfHeros\ThreeKingdom.exe" -WorkingDirectory "C:\Program Files (x86)\Steam\steamapps\common\LegendOfHeros"

# T3 triage — run after reaching the task's in-game checkpoint
Select-String -Path "C:\Program Files (x86)\Steam\steamapps\common\LegendOfHeros\BepInEx\LogOutput.log" -Pattern "GFXConf|Unhandled exception"

# T4 kill
Get-Process ThreeKingdom -ErrorAction SilentlyContinue | Stop-Process -Force
```

## Review Focus

1. **sceneLoaded delegate subscription fails at runtime** (managed→IL2CPP delegate NRE, Il2CppInterop #278). Expected: warning logged, boot continues, sweeps still happen. Test in Task 3: a menu→gameplay transition must produce a scheduled-sweep log line; if absent → implement fallback (poll active-scene handle change in `GfxBehaviour.Update`) before proceeding.
2. **Re-enable path stuck disabled**: turning a toggle OFF must restore stock, not leave effects dead. Test in Task 4 (config: `DisableBloom=false` again → Bloom count 0 and bloom visibly restored) and Task 6 (live F10 toggle-off → next summary shows 0).
3. **Sweep during scene teardown hits destroyed objects**. Expected: null-guarded, no exception, possibly lower counts. Test in Task 5: menu→gameplay→quit-to-menu cycle → zero `Unhandled exception`, summaries still logged.
4. **Name-based type lookup misses after a game update** (class renamed/stripped). Expected: one warning, rest of sweep completes with other labels counted. Test in Task 7: temporarily point one lookup at a bogus type name → single warning + summary still contains all other labels.
5. **Sweep cost / log spam**: `FindObjectsOfTypeAll` must run only when scheduled, never per frame. Test in Task 3: idle at menu 60 s → summary appears exactly once (only the scheduled sweep), no repeating lines.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/GFXConf/GFXConf.csproj` | net6.0 project, game/BepInEx refs, PostBuild copy to plugins |
| `src/GFXConf/Plugin.cs` | `[BepInPlugin]`, `Load()`, banner, `sceneLoaded` subscription |
| `src/GFXConf/Config.cs` | `GfxConfig` — all bindings per spec §4.2, `SettingToggles` map, `Save()` |
| `src/GFXConf/Sweep.cs` | `Sweeper` — scheduling (`Request`/`Tick`) + sweep groups + summary line |
| `src/GFXConf/Overlay.cs` | `GfxBehaviour` — ClassInjector MonoBehaviour, `Update()` (F10 + `Sweeper.Tick`), `OnGUI()` window |
| `docs/user-guide.md` | install/uninstall/config reference (Task 8) |

**Interfaces (defined once, consumed across tasks):**

- `GfxConfig.Bind()` → creates `ConfigFile(Path.Combine(Paths.ConfigPath, "gfxconf.cfg"), saveOnInit: false)`, binds all 18 entries, ends with `File.Save()`; `GfxConfig.Save()`; static read-only field per entry named exactly as its config key; `GfxConfig.SettingToggles : IReadOnlyDictionary<string, ConfigEntry<bool>>` keyed by concrete settings type name: `AmbientOcclusion, ChromaticAberration, DepthOfField, ScreenSpaceReflections, MotionBlur, Bloom, Fog, CloudShadows, AmbientOcclusion2D, Blur, Sharpen`.
- `Sweeper.Request(string sceneLabel, float delaySeconds)` — coalescing schedule; `Sweeper.Tick()` — runs due sweeps (called from `GfxBehaviour.Update`); `Sweeper.RunNow(string sceneLabel)` — full sweep + summary.
- Summary line (pinned format): `[GFXConf] scene={label}: AO={n}, CA={n}, DoF={n}, SSR={n}, MB={n}, Bloom={n}, SCPE.Fog={n}, SCPE.CloudShadows={n}, SCPE.AO2D={n}, SCPE.Blur={n}, SCPE.Sharpen={n}, VolumetricFog={n}, planar={n}, aura={n}, aa={n}`
- `GfxBehaviour.EnsureCreated()` — registers type in Il2Cpp, creates hidden `GameObject` + `DontDestroyOnLoad`, once.

---

### Task 1: Project scaffold — plugin loads with banner

**Files:**
- Create: `src/GFXConf/GFXConf.csproj`
- Create: `src/GFXConf/Plugin.cs`

**Interfaces:**
- Produces: build pipeline (T1) working; `Plugin : BasePlugin` with `public override void Load()` logging banner `[GFXConf] v0.1.0 loaded (com.rekyb.hvtk.gfxconf)`.

- [ ] **Step 1: Write the csproj**

SDK-style, `net6.0`, `EnableDynamicLoading`, `GameDir` property defaulting to the game root (overridable). References with `HintPath` + `Private=false`: from `$(GameDir)\BepInEx\core\` — `BepInEx.Core.dll`, `BepInEx.Unity.IL2CPP.dll`, `Il2CppInterop.Runtime.dll`; from `$(GameDir)\BepInEx\interop\` — `UnityEngine.CoreModule.dll`, `UnityEngine.InputLegacyModule.dll`, `UnityEngine.IMGUIModule.dll`, `UnityEngine.SceneManagementModule.dll`, `Unity.Postprocessing.Runtime.dll`, `sc.posteffects.runtime.dll`, `Assembly-CSharp.dll`, `Assembly-CSharp-firstpass.dll`. PostBuild target: copy `GFXConf.dll` → `$(GameDir)\BepInEx\plugins\`. (If the build errors on a missing type, the error names the module — add its HintPath.)

- [ ] **Step 2: Write minimal `Plugin.cs`**

`[BepInPlugin("com.rekyb.hvtk.gfxconf", "GFXConf", "0.1.0")]`, class `Plugin : BasePlugin`, `Load()` body = try/catch around `Log.LogInfo` banner only (nothing else yet).

- [ ] **Step 3: Run Cycle T1 → T4**

Expected: build succeeds; after launch, `T3` shows the banner line and **no** `Unhandled exception`.

- [ ] **Step 4: Commit**

```powershell
git add src/GFXConf; git commit -m "feat: GFXConf scaffold — BepInEx 6 IL2CPP plugin loads with banner"
```

### Task 2: Config schema — `gfxconf.cfg` generated with spec defaults

**Files:**
- Create: `src/GFXConf/Config.cs`
- Modify: `src/GFXConf/Plugin.cs` (`Load()` calls `GfxConfig.Bind()` first; banner additionally logs config path)

**Interfaces:**
- Consumes: `Plugin.Load()` from Task 1.
- Produces: `GfxConfig` per File Structure block — 18 `ConfigEntry` fields named exactly as their keys, `SettingToggles`, `Bind()`, `Save()`.

- [ ] **Step 1: Implement `Config.cs`**

Bind all entries from spec §4.2 (sections `[PPv2] [SCPE] [Volumetrics] [Antialiasing] [General]`), one-line description per entry, build `SettingToggles`, end `Bind()` with `File.Save()`. Bind failures must not throw (BepInEx default fallback covers parse failures — spec §4.3).

- [ ] **Step 2: Run Cycle T1 → T4, then verify the file**

Extra check after T3:

```powershell
Get-Content "C:\Program Files (x86)\Steam\steamapps\common\LegendOfHeros\BepInEx\config\gfxconf.cfg"
```

Expected: all 18 keys present, e.g. `DisableAmbientOcclusion = true`, `DisableBloom = false`, `OverrideMode = KeepOriginal`, `DelaySeconds = 2`; banner shows config path; no `Unhandled exception`.

- [ ] **Step 3: Commit**

```powershell
git add src/GFXConf; git commit -m "feat: config schema (spec 4.2) — gfxconf.cfg with 18 entries"
```

### Task 3: Behaviour pump + sceneLoaded scheduling (sweep skeleton)

**Files:**
- Create: `src/GFXConf/Overlay.cs` (Behaviour only — no window yet)
- Create: `src/GFXConf/Sweep.cs` (`Sweeper` scheduling + `RunNow` skeleton)
- Modify: `src/GFXConf/Plugin.cs` (register Behaviour, subscribe `sceneLoaded`, initial `Request`)

**Interfaces:**
- Consumes: `GfxConfig.ReapplyOnSceneLoad`, `DelaySeconds`, `EnableF10Overlay` (Task 2).
- Produces: `GfxBehaviour.EnsureCreated()`; `Sweeper.Request/Tick/RunNow` (skeleton logs per-group-zero summary in the pinned format); `Plugin.OnSceneLoaded(Scene, LoadSceneMode)`.

- [ ] **Step 1: Implement scheduling skeleton**

`GfxBehaviour : MonoBehaviour` via `ClassInjector.RegisterTypeInIl2Cpp<GfxBehaviour>()`, hidden `GameObject` + `DontDestroyOnLoad`; `Update()` calls `Sweeper.Tick()` (F10 logic stubbed to no-op). `Sweeper.Request` logs `[GFXConf] scene={label} sweep in {delay}s`, then stores label + `dueTime = Time.realtimeSinceStartup + delaySeconds` (coalesce: newest wins); `Tick` runs `RunNow(label)` when due; `RunNow` = try/catch + **one summary line only** (pinned format, all labels 0 for now — spec §4.1 "one summary line per sweep").

- [ ] **Step 2: Wire `Plugin.Load()` and `sceneLoaded`**

Order: `Bind()` → banner → `EnsureCreated()` → subscribe `sceneLoaded` (whole subscription in try/catch — Review Focus 1) → `Sweeper.Request("startup", GfxConfig.DelaySeconds.Value)`. `OnSceneLoaded`: try/catch; if `ReapplyOnSceneLoad` → `Sweeper.Request(scene.name, DelaySeconds)`.

- [ ] **Step 3: Run Cycle T1 → T4 with Review Focus 1 + 5 checks**

Expected: startup sweep line ~2 s after boot; enter gameplay → scheduled sweep for the new scene; idle 60 s → no extra summary lines (Focus 5); if scene transition produces **no** sweep line → subscription failed: log will show the caught warning → implement the handle-polling fallback in `GfxBehaviour.Update()` (track `SceneManager.GetActiveScene().handle`, request sweep on change) before proceeding; no `Unhandled exception` ever.

- [ ] **Step 4: Commit**

```powershell
git add src/GFXConf; git commit -m "feat: IL2CPP behaviour pump + sceneLoaded sweep scheduling"
```

### Task 4: Settings sweep — PPv2 + SCPE groups with real counts

**Files:**
- Modify: `src/GFXConf/Sweep.cs` (replace skeleton body with settings-group sweep)

**Interfaces:**
- Consumes: `GfxConfig.SettingToggles` (Task 2), `Sweeper.RunNow` skeleton (Task 3).
- Produces: settings groups counted under pinned labels (`AO`, `CA`, `DoF`, `SSR`, `MB`, `Bloom`, `SCPE.*`).

- [ ] **Step 1: Implement the settings sweep**

Per group in its own try/catch: `Resources.FindObjectsOfTypeAll<PostProcessProfile>()` → skip null `profile.settings` entries → look up `settings.GetType().Name` in `SettingToggles`:

- toggle **true** → on first modification capture original `active` into `Dictionary<PostProcessEffectSettings,bool>`, then `settings.active = false; settings.enabled.value = false; settings.enabled.OverrideState = true;` count++
- toggle **false** → release: `settings.enabled.OverrideState = false;` restore `active` from the captured value (fallback if uncaptured: `active = true`); count 0 — Review Focus 2 (stock state restored exactly, never force-enabled)

Null-guard destroyed profiles. Component groups and `aa` label stay 0 for now (Task 5).

- [ ] **Step 2: Run Cycle T1 → T4 at menu (spec test 1)**

Expected: banner + one summary line, counts as loaded (likely >0 for resource profiles), no `Error`/`Unhandled exception`.

- [ ] **Step 3: Spec test 3 — Bloom toggle proof**

Edit `gfxconf.cfg` → `DisableBloom = true`, relaunch, enter gameplay: summary shows `Bloom > 0` (scan: 4 at level73 + resources). Then set back to `false`, relaunch: `Bloom = 0` **and** bloom visibly restored (Review Focus 2). Kill after.

- [ ] **Step 4: Commit**

```powershell
git add src/GFXConf; git commit -m "feat: PPv2 + SCPE settings sweep with disable/release logic and summary counts"
```

### Task 5: Component groups + AA override (spec tests 2 and 4)

**Files:**
- Modify: `src/GFXConf/Sweep.cs` (component lookup group + AA group)

**Interfaces:**
- Consumes: `GfxConfig.DisableVolumetricFog/DisablePlanarReflections/DisableAura2`, `OverrideMode`; sweep summary skeleton.
- Produces: `VolumetricFog`, `planar`, `aura`, `aa` labels populated.

- [ ] **Step 1: Implement component + AA groups**

Type lookup by full name across loaded assemblies (once, cached; missing → `[GFXConf] type not found: {name}` once, group skipped): `VolumetricFogAndMist.VolumetricFog`, `Ceto.PlanarReflection`, Aura2 `Aura`/`AuraVolume`/`AuraCamera` → non-generic `FindObjectsOfType(type)` → `Behaviour.enabled = false` per toggle. AA: for each `PostProcessLayer`, if `OverrideMode != "KeepOriginal"` → `Enum.TryParse<AntialiasingMode>` → assign `antialiasingMode`, count into `aa` (invalid string → `LogWarning` once, skip — spec §4.3).

- [ ] **Step 2: Run Cycle T1 → T4 with spec test 2 (level73)**

Expected in gameplay summary: `AO`, `CA`, `SCPE.Fog`, `VolumetricFog` all > 0 (scan: AO 4, CA 4, Fog 4, VolumetricFog 4 at level73), `planar` > 0 on level1/2, `aura = 0` (none exist — future-proof).

- [ ] **Step 3: Spec test 4 — AA override**

Config → `OverrideMode = FXAA`, relaunch, enter gameplay: summary `aa = ` number of layers changed (level4-14,73 have layers), no errors. Revert to `KeepOriginal`.

- [ ] **Step 4: Review Focus 3 — teardown safety**

Menu → gameplay → quit-to-menu (or rapid scene change): expected zero `Unhandled exception`, a summary still logged per scene (counts may differ).

- [ ] **Step 5: Commit**

```powershell
git add src/GFXConf; git commit -m "feat: component disables (VolumetricFog/Planar/Aura2) + antialiasing override"
```

### Task 6: F10 overlay (spec test 5)

**Files:**
- Modify: `src/GFXConf/Overlay.cs` (`Update()` F10 detection, `OnGUI()` window)
- Modify: `src/GFXConf/Config.cs` (only if a helper to enumerate entries is needed)

**Interfaces:**
- Consumes: `GfxConfig` entries + `Save()`; `Sweeper.Request/RunNow` (immediate sweep: `Request(label, 0f)`).
- Produces: working overlay; gate `EnableF10Overlay`.

- [ ] **Step 1: Implement overlay**

`Update()`: `Input.GetKeyDown(KeyCode.F10)` → toggle visible (no-op when `EnableF10Overlay = false`). `OnGUI()`: when visible, `GUILayout.Window` (const `WindowId = 4170`) with one toggle per config entry grouped by section, F10-hint footer, last summary label; on any change → `GfxConfig.Save()` + immediate sweep; keep drawing-only work inside `OnGUI` (no per-frame cost while closed — rules §3).

- [ ] **Step 2: Run Cycle T1 → T4 (spec test 5)**

Expected: F10 opens/closes; toggle `DisableBloom` off→on live → `gfxconf.cfg` updated on disk **and** next summary reflects it (Review Focus 2: toggle off → count 0, bloom back); overlay closed → game input unaffected; no errors.

- [ ] **Step 3: Commit**

```powershell
git add src/GFXConf; git commit -m "feat: F10 IMGUI overlay — live toggles, config save, immediate sweep"
```

### Task 7: Crash safety (spec test 7 + Review Focus 4)

**Files:**
- Modify: `src/GFXConf/Sweep.cs` (kept debug hook)

**Interfaces:**
- Consumes: existing try/catch structure.
- Produces: `const bool ForceTestException = false` inside `RunNow` (when true, throws before sweep — must remain `false` in final code).

- [ ] **Step 1: Force-exception path**

Set `ForceTestException = true`, rebuild, launch: expected `LogWarning` with caught exception, game keeps running, **no** `Unhandled exception` in log. Revert to `false`, rebuild.

- [ ] **Step 2: Review Focus 4 — missing type path**

Temporarily point one component lookup name at `GFXConf.DoesNotExist`: expected exactly one `type not found` warning, summary still counts all other labels, game continues. Restore the real name, rebuild.

- [ ] **Step 3: Verify clean + commit**

Cycle T1 → T4 once more (must be clean), then:

```powershell
git add src/GFXConf; git commit -m "test: crash-safety paths — forced exception + missing type both degrade gracefully"
```

### Task 8: Documentation, uninstall test, full regression (spec tests 6 + §6 rerun)

**Files:**
- Create: `docs/user-guide.md` (spec §7: install/uninstall/config reference — all 18 keys, defaults, F10 usage, log-evidence how-to)
- Modify: `README.md` (status: implemented; link user guide)
- Modify: `docs/superpowers/specs/2026-10-07-gfxconf-design.md` (no content change needed — verify §7 deliverables all exist)

**Interfaces:**
- Consumes: everything from Tasks 1–7.
- Produces: shipped docs; final evidence run.

- [ ] **Step 1: Spec test 6 — uninstall**

Rename `BepInEx` + `winhttp.dll` in game dir (e.g. append `.bak`), launch → game boots stock, no BepInEx lines in output; restore both.

- [ ] **Step 2: Full §6 regression with evidence**

Run tests 1–5 in one session (boot → menu summary → level73 counts → Bloom flip → AA override → F10 live toggle), collect `Select-String` evidence lines for each.

- [ ] **Step 3: Write `docs/user-guide.md` + update README**

Install (BepInEx required, DLL placement), uninstall (delete `BepInEx/` + `winhttp.dll` = stock), config table (18 keys/defaults from §4.2), overlay usage, troubleshooting (where `LogOutput.log` is, what a clean run looks like).

- [ ] **Step 4: Commit (local) and present everything for review**

```powershell
git add -A; git commit -m "docs: user guide + README status; GFXConf v0.1.0 complete per spec"
```

Present all unpushed commits (message/files/diff summary) to the user; **push only after explicit approval.**
