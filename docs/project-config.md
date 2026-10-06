# Project Config — hvtk-graphics-config (TKLowFX)

Date: 2026-10-07 · Status: active
Single reference for toolchain, paths, metadata, and commands.
If a value changes, update this file in the same commit.

## 1. Identity

| Field | Value |
|---|---|
| Repository | <https://github.com/rekyb/hvtk-graphics-config.git> |
| Branch | `main` (direct commits, keep in sync with `origin/main`) |
| Git identity (local) | `rekyb <rekyb@users.noreply.github.com>` |
| Plugin GUID | `com.rekyb.hvtk.gfxconf` (locked — never change after v0.1.0) |
| Plugin name | `TKLowFX` |
| Version | `0.1.0` (semver; bump per release) |
| Config file | `<game>/BepInEx/config/tklowfx.cfg` |

## 2. Toolchain

| Tool | Version / value |
|---|---|
| .NET SDK | 10.0.401 (installed) |
| Target framework | `net6.0` |
| Project style | SDK-style csproj, `EnableDynamicLoading=true` |
| Language | latest C# supported by the SDK for net6.0 |
| Dependencies | BepInEx core + game interop DLLs only — **no NuGet packages** |
| Analysis scripts | Python + UnityPy (in `tools/`) |

## 3. Game & BepInEx paths (read-only except `BepInEx/plugins`)

| Thing | Path |
|---|---|
| Game root | `C:\Program Files (x86)\Steam\steamapps\common\LegendOfHeros\` |
| Game exe | `ThreeKingdom.exe` (launch spawns **two** processes) |
| Unity data | `ThreeKingdom_Data\` (never write) |
| BepInEx version | 6.0.0-be.788 Unity IL2CPP win-x64 (bleeding edge; pre.2 fails on metadata v31) |
| Interop refs (compile-time) | `BepInEx\interop\` (127 regenerated DLLs) |
| BepInEx core refs | `BepInEx\core\` |
| Plugin deploy dir | `BepInEx\plugins\` ← **only** build output target |
| Runtime log | `BepInEx\LogOutput.log` |
| BepInEx config | `BepInEx\config\BepInEx.cfg` (console/logging toggles) |
| Plugin config | `BepInEx\config\tklowfx.cfg` (auto-created on first run) |

Key interop assemblies referenced by the plugin:

| Assembly | Contains |
|---|---|
| `Unity.Postprocessing.Runtime.dll` | PPv2 `PostProcessProfile` / `PostProcessLayer` / effect settings |
| `sc.posteffects.runtime.dll` | SCPE effects (Fog, CloudShadows, AO2D, Blur, Sharpen…) |
| `Assembly-CSharp.dll` / `Assembly-CSharp-firstpass.dll` | `VolumetricFog`, Ceto `PlanarReflection`, game code |
| `Aura2_Core.dll` | Aura2 (future-proof, 0 instances) |

## 4. Repository layout

```
test-mod\  (= repo root)
├── README.md
├── .gitignore                  # blocks *.assets, bin/, obj/, editor junk
├── docs\
│   ├── hvtk-context.md             # original performance context (converted)
│   ├── development-rules.md        # rules all code must follow
│   ├── project-config.md           # this file
│   └── superpowers\specs\2026-10-07-tklowfx-design.md   # approved spec
├── src\TKLowFX\               # plugin project (Plugin/Config/Sweep/Overlay .cs)
├── tools\                     # UnityPy analysis scripts
│   └── out\catalog.json           # asset scan catalog
└── originals\                 # NOT committed (git-ignored binaries + source JSON)
```

## 5. Build & deploy commands

```powershell
# build
dotnet build C:\Users\rekyb\Desktop\test-mod\src\TKLowFX\TKLowFX.csproj -c Release

# deploy (also automated via csproj PostBuild copy)
Copy-Item ...\src\TKLowFX\bin\Release\net6.0\TKLowFX.dll `
  "C:\Program Files (x86)\Steam\steamapps\common\LegendOfHeros\BepInEx\plugins\" -Force

# test launch (expect two ThreeKingdom.exe processes; kill after test)
Start-Process -FilePath "C:\Program Files (x86)\Steam\steamapps\common\LegendOfHeros\ThreeKingdom.exe" `
  -WorkingDirectory "C:\Program Files (x86)\Steam\steamapps\common\LegendOfHeros"

# triage log
Select-String -Path "C:\Program Files (x86)\Steam\steamapps\common\LegendOfHeros\BepInEx\LogOutput.log" `
  -Pattern "TKLowFX|Unhandled exception|LogError"
```

## 6. csproj conventions

- Reference BepInEx core + interop DLLs via `HintPath` from a single
  `$(GameDir)` property (defined once, overridable) — no DLLs copied into the
  repo.
- `Private=false` on all game/BepInEx references (never copy them to output).
- PostBuild target: copy `TKLowFX.dll` → `$(GameDir)\BepInEx\plugins\`.
- Warnings-as-errors on our own code where practical; no `unsafe` blocks.

## 7. Test checklist pointer

The acceptance tests live in the spec §6
(`docs/superpowers/specs/2026-10-07-tklowfx-design.md`): boot smoke test,
scene coverage counts, toggle proof, AA override, F10 overlay, uninstall,
crash safety. Run the relevant ones before marking anything done and attach
`LogOutput.log` evidence.

## 8. Environment notes

- BepInEx BE #788 log shows non-fatal warnings (`Class::Init signatures have
  been exhausted`, some method-restores failed) — known/benign, chainloader
  still completes.
- Game cleanup stutter (~1033 ms, 17,017 assets unloaded) is **out of scope**
  (see `docs/hvtk-context.md`).
- Game updates may bump Unity/metadata version → BepInEx and interop must be
  regenerated first; plugin tolerates missing types by design
  (see `docs/development-rules.md` §2.5/§2.6).
