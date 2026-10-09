# Development Rules — hvtk-graphics-config (GFXConf)

Date: 2026-10-07 · Status: active
Applies to all code, scripts, and configs added to this repository from now on.
Derived from research on BepInEx modding best practices (sources at the end).

## 1. Hard rules (project invariants)

1. **Never modify game asset files.** No byte-patching, no writes outside
   `BepInEx/` in the game folder. Runtime-only effects = removable by deleting
   the plugin. If a change ever requires touching assets, stop and re-plan.
2. **Build output may only write to `<game>/BepInEx/plugins/`.** Nothing else
   in the game folder is a build target.
3. **The game must never crash because of this plugin.** Every line that
   touches game state runs inside a try/catch that logs a warning and returns.
   An exception escaping into Unity/BepInEx callbacks is a failure, even if the
   feature "would have worked".
4. **Config file is the source of truth.** All behavior is driven by
   `BepInEx/config/gfxconf.cfg`; the F10 overlay only edits that file. No
   hidden state.
5. **Never re-enable or delete game content we didn't target** — no blanket
   `m_Enabled = false`, no shader deletion, no profile asset edits (from the
   original context: exact PathID/field discipline still applies in spirit).
6. **Do not commit game DLLs** (interop, Assembly-CSharp, etc.) — license and
   repo-hygiene issue. `.gitignore` already blocks binaries; keep it that way.
7. **Verify before claiming.** "Fixed", "works", "disabled" claims require
   evidence: build output + `LogOutput.log` lines + the sweep summary counts.

## 2. BepInEx best practices (from research → applied here)

### 2.1 Touch as little game code as possible
- Prefer **data manipulation over code patching**: set `active`/`enabled`
  values on effect objects rather than hooking rendering methods.
- **Harmony is last resort.** If ever needed: Postfix over Prefix, never
  skip/cancel the original method, never a Transpiler, whole patch body in
  try/catch. (Current design needs **zero Harmony patches** — keep it that way
  unless a proven blocker appears.)
- Look up types **defensively by name** across loaded assemblies; a missing
  type (game update, stripped build) = skip + log once, not an error.

### 2.2 Identity & versioning
- GUID in reverse-domain notation, **never changed after first release**;
  DLL name equally stable (BepInEx keys duplicates/dependencies off GUID).
- Version follows semver (`major.minor.patch`); bump on every release,
  record changes in the README/changelog.
- Current identity (locked once v0.1.0 ships):

  | Field | Value |
  |---|---|
  | GUID | `com.rekyb.hvtk.gfxconf` |
  | Name | `GFXConf` |
  | Version | `0.3.0` |

### 2.3 Logging
- Use the plugin's `ManualLogSource` (goes to `BepInEx/LogOutput.log`), never
  `Console.WriteLine` or bare `Debug.Log`.
- Levels: `LogDebug` for per-object detail (behind a config/debug flag),
  `LogInfo` for lifecycle + one **summary line per sweep**,
  `LogWarning` for caught errors/missing types, `LogError` almost never.
- One `[GFXConf]` prefix, one line per event — no spam, no per-frame logging
  (the game log already has warnings; don't make triage harder).
- A clean log is a test artifact: after any run, `LogOutput.log` must show no
  `Unhandled exception` and no `LogError` from us.

### 2.4 Configuration
- Bind every option with `ConfigFile.Bind(section, key, default, description)`
  — the description text is the documentation the config UI/user sees.
- Defaults must be the **safe performance preset** from the spec (§4.2);
  a fresh install = spec defaults, no manual editing required.
- Config read failures fall back to defaults (BepInEx does this natively);
  never throw from config binding.
- Config changes apply live (overlay) or on next launch — never require
  reinstalling anything.

### 2.5 IL2CPP-specific caution
- Interop assemblies are **regenerated** by BepInEx on first run / game
  update; code must tolerate a regenerated interop (no assumptions pinned to
  a specific interop build; API looked up by name where fragile).
- IL2CPP delegates/event subscriptions can throw `NullReferenceException`
  (known Il2CppInterop issue) → subscribe inside try/catch, verify the
  subscription actually took.
- Unity object lifetime: destroyed objects throw on access → null-check with
  Unity's overloaded `==`, and never cache scene objects across scene loads.
- Avoid per-frame work: sweeps run on scene load + one delayed call only;
  `Update` reserved for the overlay and only while it's open.

### 2.6 Isolation & future-proofing (survives game updates)
- Every effect group independently toggleable → one broken group can be
  disabled without killing the plugin.
- All game-object queries wrapped and counted; counts logged so a game update
  that moves/removes a class shows up as a changed count, not a crash.
- Clean uninstall documented: delete `BepInEx/` + `winhttp.dll` = stock game
  (also the bug-isolation trick: reproduce issues with the plugin off before
  blaming the mod).

## 3. Code & project conventions

- **Language/target:** C# , `net6.0`, SDK-style csproj, `EnableDynamicLoading`.
- **Zero third-party dependencies** beyond BepInEx + interop references.
  Anything new needs explicit approval first.
- **File layout** in `src/GFXConf/`:
  - `Plugin.cs` — attributes, `Awake`, subscriptions, lifecycle
  - `Config.cs` — all `ConfigEntry` bindings in one place (single schema
    reference, mirrors spec §4.2)
  - `Sweep.cs` — the sweep logic per effect group
  - `Overlay.cs` — F10 IMGUI window
- No `Update()` logic outside `Overlay.cs`; no allocations in hot paths.
- Naming: PascalCase types/methods, camelCase locals; config keys exactly as
  spec §4.2 (`DisableAmbientOcclusion`, …) — spec and `Config.cs` must match
  word-for-word.
- Comments explain *why*, not *what*; no commented-out code committed.

## 4. Workflow rules

1. **Plan → build → verify → commit → review → push.** No direct pushes of
   untested changes to `main`; every *code* commit must build
   (`dotnet build`) and doc-only commits must keep spec/config/docs
   consistent. **`git push` requires explicit user approval per push** —
   commit locally first, then present the commit (message, files, diff
   summary) for the user's review; never push, merge, or publish a PR on
   your own initiative.
2. Every behavior change updates the spec/docs in the same commit if it
   changes observable behavior (config keys, defaults, log formats).
3. Before claiming a feature done: run the relevant test from spec §6 and
   paste the log evidence in the session/commit message.
4. Game-launching tests: launch from the game folder, expect **two**
   `ThreeKingdom.exe` processes, always kill them after the test.
5. Never edit files under the game's `ThreeKingdom_Data/` — reads only.

## 5. Research sources

- BepInEx official docs — *Writing a basic plugin* tutorial series
  (setup, project creation, logging, configuration; GUID/semver permanence):
  <https://docs.bepinex.dev/articles/dev_guide/plugin_tutorial/index.html>
- BepInEx — *Complete Guide to Updating, Maintaining and Future-Proofing
  BepInEx Mods* (defensive code, no hardcoding, clean mod environment,
  backups before updates): <https://bepinex.org/complete-guide-to-updating-maintaining-and-future-proofing-bepinex-mods>
- BepInEx — *Debugging, Error Handling and Advanced Logging* (log levels,
  log-first triage, defensive exception handling):
  <https://bepinex.org/complete-guide-to-debugging-error-handling-and-advanced-logging-in-bepinex-for-unity-modding/>
- BepInEx docs — *Patching game methods at runtime* (HarmonyX vs MonoMod):
  <https://docs.bepinex.dev/articles/dev_guide/runtime_patching.html>
- Community guide (Lehti/Shinter, Steam) — "plan your plugin so that it
  touches as little game code as possible"; prefer Postfix over skipping
  originals; never commit referenced game DLLs:
  <https://steamcommunity.com/sharedfiles/filedetails/?id=2576217807>
- Harmony beginner guide (EliteMasterEric) — uncaught exceptions in Harmony
  patches almost always crash; wrap in try/catch:
  <https://gist.github.com/EliteMasterEric/cc9f0271af9410aec32ead637efe7741>
- Il2CppInterop issue #278 — IL2CPP delegate/event `NullReferenceException`
  quirk: <https://github.com/BepInEx/Il2CppInterop/issues/278>
