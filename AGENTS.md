# Project instructions — hvtk-graphics-config (GFXConf)

Config-driven low-effects **BepInEx 6 IL2CPP plugin** for *Heroes' Vow: Three
Kingdoms* (Steam, Unity 2021.3, IL2CPP metadata v31), targeting low-performance
devices generally (reference hardware: Ryzen 5 5500U iGPU). Design/spec phase →
implementation follows the approved docs below.

## Read before working

| Doc | Purpose |
|---|---|
| `docs/development-rules.md` | Hard rules + BepInEx best practices every change must follow |
| `docs/project-config.md` | Toolchain, paths, GUID/metadata, build/deploy/test commands |
| `docs/superpowers/specs/2026-10-07-gfxconf-design.md` | Approved spec (config schema §4.2, test plan §6) |
| `docs/hvtk-context.md` | Original performance context and findings |

## Non-negotiable rules

1. **Never modify game asset files.** Reads only under `ThreeKingdom_Data\`.
   The only allowed write into the game folder is `BepInEx\plugins\` (plugin
   DLL) and BepInEx-generated config/log files.
2. **The game must never crash because of the plugin.** Every touch of game
   state runs inside try/catch → `LogWarning`, never rethrow. Zero Harmony
   patches unless a proven blocker appears (then Postfix-only, body wrapped).
3. **Config file is the source of truth** (`BepInEx/config/gfxconf.cfg`); all
   toggles come from spec §4.2 word-for-word.
4. **Verify before claiming.** "Works/fixed/disabled" requires evidence:
   `dotnet build` output + `LogOutput.log` lines + sweep summary counts.
5. **No NuGet dependencies** beyond BepInEx core + game interop references.
   Never commit game DLLs or binaries (`.gitignore` enforces).
6. **Identity locked:** GUID `com.rekyb.hvtk.gfxconf`, DLL `GFXConf.dll`,
   semver versions. Never rename after first release.
7. **No push without approval.** Never `git push` (any branch, including
   `main`) unless the user explicitly says so in the current conversation.
   Workflow: commit locally → present the commit (message + file list + diff
   summary) to the user for review → push only after explicit approval.
   The same applies to PRs: create/draft them if asked, but never merge or
   push without a green light.

## Workflow

- Code lives in `src/GFXConf/` as `Plugin.cs` / `Config.cs` / `Sweep.cs` /
  `Overlay.cs` (see rules §3).
- Loop: plan → `dotnet build -c Release` → deploy to `BepInEx\plugins\` →
  test (launch from game dir, **two** `ThreeKingdom.exe` processes, kill
  after) → triage `BepInEx\LogOutput.log` for `GFXConf|Unhandled exception`
  → commit → **present for review → push only after explicit user approval**.
- Doc changes that affect observable behavior (config keys, defaults, log
  formats) ship in the same commit as the code change.
- Out of scope: asset patching, FPS/frametime logging, the ~1033 ms
  asset-cleanup stutter.
