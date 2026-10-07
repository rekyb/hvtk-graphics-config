# GFXConf Overlay UX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the F10 overlay draggable by its title bar (session-only, snaps back on open), remove the confusing footer, show toggle labels as key names only, and stop clicks passing through to the game UI.

**Architecture:** All changes are inside the IL2CPP-injected `GfxBehaviour` in `Overlay.cs`. The dead `GUILayout.Window` path is left in place (it falls back to the panel path on this build); drag is implemented manually for the fixed panel using `Event.current` against the header rect. Visibility transitions are centralized into `OpenOverlay()`/`CloseOverlay()` so game input (the uGUI `EventSystem`) is always restored. `Sweeper.LastSummary` — only used by the removed footer — is deleted.

**Tech Stack:** C# / net6.0 (SDK 10.0.401), BepInEx 6.0.0-be.788 Unity IL2CPP, game interop assemblies, Unity IMGUI. No NuGet packages.

**Spec:** `docs/superpowers/specs/2026-10-07-overlay-ux-design.md` (approved 2026-10-07 — this plan argues from it; executors read both).

## Global Constraints

- Identity unchanged: GUID `com.rekyb.hvtk.gfxconf`, name `GFXConf`, version `0.1.0`, DLL `GFXConf.dll`.
- Target `net6.0`, `EnableDynamicLoading=true`; references only from `<game>/BepInEx/core/` and `<game>/BepInEx/interop/` via HintPath with `Private=false`. **No NuGet packages.**
- The only write into the game folder is `<game>/BepInEx/plugins/` (build output) plus BepInEx-generated `config/`/logs. Reads only under `ThreeKingdom_Data\`.
- **No config schema change.** Do not add, rename, or re-default any `gfxconf.cfg` key.
- **Zero Harmony patches.** Every game-state touch (EventSystem lookup/disable/restore) runs in its own try/catch → `LogWarning`, never rethrow.
- No per-frame allocations; `OnGUI` stays draw-only while hidden; no per-frame warnings for a persistent failure.
- Log prefix `[GFXConf]`; the `overlay opened` / `overlay closed` strings are pinned byte-identical.
- Tests are runtime game tests (no test framework — NuGet ban): evidence = build output + `LogOutput.log` lines. Launch = **two** `ThreeKingdom.exe` processes; kill after every test.
- Docs that change with observable behavior ship in the same commit (development-rules §4.2).
- **Every commit is local.** Present message + files + diff summary to the user; push only on explicit approval (AGENTS.md rule 7).

## Standard Test Cycle (referenced by every task as Cycle T1–T4)

```powershell
# T1 build (PostBuild copies DLL to plugins)
dotnet build "C:\Users\rekyb\Desktop\hvtx-gfx-conf\src\GFXConf\GFXConf.csproj" -c Release
# expect: "Build succeeded"; output shows GFXConf.dll copied to <game>\BepInEx\plugins\

# T2 launch from game dir (expect TWO ThreeKingdom.exe processes)
Start-Process -FilePath "C:\Program Files (x86)\Steam\steamapps\common\LegendOfHeros\ThreeKingdom.exe" -WorkingDirectory "C:\Program Files (x86)\Steam\steamapps\common\LegendOfHeros"

# T3 triage — run after reaching the task's in-game checkpoint
Select-String -Path "C:\Program Files (x86)\Steam\steamapps\common\LegendOfHeros\BepInEx\LogOutput.log" -Pattern "GFXConf|Unhandled exception"

# T4 kill
Get-Process ThreeKingdom -ErrorAction SilentlyContinue | Stop-Process -Force
```

## Review Focus

1. **Close X vs. drag:** clicking the header X must close the overlay and must never start a drag. Test in Task 3 (click X after a drag → closes, no move).
2. **Drag near a screen edge:** the panel must stay fully on-screen. Test in Task 3 (drag past an edge → clamped).
3. **Reopen after drag:** the overlay must snap back to the default top-left. Test in Task 3 (drag, close, reopen → default corner).
4. **EventSystem missing/absent:** if `EventSystem` is not found, the overlay must still open/close with exactly one `type not found` warning and no crash. Test in Task 2 (force the name missing → single warning, overlay works).
5. **Draw-failure restore:** if the overlay is force-hidden by the `OnGUI` catch, game input must be restored (EventSystem not left disabled). Test in Task 2 (trigger the catch → click a game button after → it responds).

---

## File Structure

| File | Responsibility (this plan) |
|---|---|
| `src/GFXConf/Overlay.cs` | All UI: footer removal, key-only labels, drag state, `OpenOverlay`/`CloseOverlay`, EventSystem block/restore |
| `src/GFXConf/Sweep.cs` | Remove the now-unused `LastSummary` property + assignment |
| `docs/user-guide.md` | §3 overlay description kept in sync per task |

**Interfaces (defined once, consumed across tasks):**

- `GfxBehaviour.OpenOverlay()` — static; the single open path: sets `_visible = true`, resets overlay position/drag state, blocks game input, logs `[GFXConf] overlay opened`.
- `GfxBehaviour.CloseOverlay()` — static; the single close path: sets `_visible = false`, restores game input, logs `[GFXConf] overlay closed`.
- `GfxBehaviour.BlockGameInput()` / `RestoreGameInput()` — static; disable/restore `UnityEngine.EventSystems.EventSystem` components found by name; both try/catch, never throw.
- `GfxBehaviour._panelPos : Vector2` — current panel top-left in GUI space (default `(PanelRect.x, PanelRect.y)`).

---

### Task 1: Overlay cleanup — no footer, key-only labels

**Files:**
- Modify: `src/GFXConf/Overlay.cs`
- Modify: `src/GFXConf/Sweep.cs`
- Modify: `docs/user-guide.md`

**Interfaces:**
- Consumes: nothing new.
- Produces: overlay with no footer; `DrawToggle` label is the bare key; `Sweeper.LastSummary` removed (no later task may reference it).

- [ ] **Step 1: Make `DrawToggle` show the key only**

In `src/GFXConf/Overlay.cs`, change the label line in `DrawToggle`:

```csharp
var next = GUILayout.Toggle(current, entry.Definition.Key);
```

(`OverrideMode` at `DrawOverrideMode` and `DelaySeconds` at `DrawDelaySeconds` are unchanged — they keep showing their values.)

- [ ] **Step 2: Remove the footer from `DrawPanel`**

In `DrawPanel`: delete the `var footer = new Rect(...)` line; change the scroll rect to fill down to the panel bottom:

```csharp
var scroll = new Rect(
    panel.x + 2f, header.yMax + 2f, panel.width - 4f,
    Mathf.Max(40f, panel.yMax - header.yMax - 4f));
```

Then delete the whole `GUILayout.BeginArea(footer) { DrawFooter(); } finally { GUILayout.EndArea(); }` block. Keep `FillOpaque`, the `GUI.Box(panel, …)`, the scroll view, and the header/close X.

- [ ] **Step 3: Remove the footer from `DrawWindow` and delete `DrawFooter`**

Delete the `DrawFooter();` call in `DrawWindow`, then delete the entire `DrawFooter()` method.

- [ ] **Step 4: Remove now-dead members**

Delete the `FooterHeight` const and the `_wrapStyle` field; remove the `_wrapStyle = new GUIStyle(...)` line inside `DrawControls` (keep `_sectionStyle`).

- [ ] **Step 5: Delete `Sweeper.LastSummary`**

In `src/GFXConf/Sweep.cs`: delete the `LastSummary` property (its XML-doc block + declaration) and the `LastSummary = summary;` assignment in `RunNow`. Leave the `GfxConfig.LogSource?.LogInfo(summary);` line untouched.

- [ ] **Step 6: Sync the user-guide intro**

In `docs/user-guide.md` §3, change the sentence fragment "every config key as a live control, plus the last sweep summary at the bottom." to "every config key as a live control."

- [ ] **Step 7: Run Cycle T1–T4**

Expected: build succeeds; in game the overlay has **no footer/summary**, and every toggle row shows only its key name; `OverrideMode`/`DelaySeconds` still show values; T3 shows `overlay opened`/`overlay closed` and no `Unhandled exception`.

- [ ] **Step 8: Commit**

```powershell
git add src/GFXConf/Overlay.cs src/GFXConf/Sweep.cs docs/user-guide.md
git commit -m "feat(overlay): remove footer, show toggle key names only; drop LastSummary"
```

---

### Task 2: Block click-through — centralized open/close + EventSystem disable

**Files:**
- Modify: `src/GFXConf/Overlay.cs`
- Modify: `docs/user-guide.md`

**Interfaces:**
- Consumes: existing `Update()` F10 handling, `CloseOverlay()`.
- Produces: `OpenOverlay()`, `CloseOverlay()` (rewritten), `BlockGameInput()`, `RestoreGameInput()`, `ResolveEventSystemType()`, `_blockedEventSystems : List<Behaviour>`. Task 3 adds to `OpenOverlay()`.

- [ ] **Step 1: Add the using and fields**

Add `using System.Collections.Generic;` to `Overlay.cs`. Add:

```csharp
private static readonly List<Behaviour> _blockedEventSystems = new();
private static Type _eventSystemType;
private static bool _eventSystemTypeResolved;
```

- [ ] **Step 2: Add `ResolveEventSystemType()`**

Scan `AppDomain.CurrentDomain.GetAssemblies()` for `"UnityEngine.EventSystems.EventSystem"`, cache the result (hit **and** miss, resolved-once), and log `[GFXConf] type not found: UnityEngine.EventSystems.EventSystem` exactly once when it is missing. Each `assembly.GetType` in its own try/catch → `LogWarning` + `continue`.

- [ ] **Step 3: Add `BlockGameInput()` and `RestoreGameInput()`**

```csharp
private static void BlockGameInput()
{
    try
    {
        var type = ResolveEventSystemType();
        if (type == null) return;
        var found = UnityEngine.Object.FindObjectsOfType(
            Il2CppInterop.Runtime.Il2CppType.From(type));
        if (found == null) return;
        foreach (var obj in found)
        {
            if (obj == null) continue;
            var behaviour = obj as Behaviour
                ?? (Activator.CreateInstance(type, obj.Pointer) as Behaviour);
            if (behaviour == null) continue;
            behaviour.enabled = false;
            _blockedEventSystems.Add(behaviour);
        }
    }
    catch (Exception ex)
    {
        GfxConfig.LogSource?.LogWarning($"[GFXConf] input block failed: {ex}");
    }
}

private static void RestoreGameInput()
{
    try
    {
        foreach (var behaviour in _blockedEventSystems)
        {
            if (behaviour != null) behaviour.enabled = true;
        }
    }
    catch (Exception ex)
    {
        GfxConfig.LogSource?.LogWarning($"[GFXConf] input restore failed: {ex}");
    }
    finally
    {
        _blockedEventSystems.Clear();
    }
}
```

(The wrapper re-wrap note is the same interop caveat as `Sweep.DisableComponents`.)

- [ ] **Step 4: Add `OpenOverlay()` and rewrite `CloseOverlay()`**

```csharp
private static void OpenOverlay()
{
    _visible = true;
    BlockGameInput();
    GfxConfig.LogSource?.LogInfo("[GFXConf] overlay opened");
}

private static void CloseOverlay()
{
    _visible = false;
    RestoreGameInput();
    GfxConfig.LogSource?.LogInfo("[GFXConf] overlay closed");
}
```

- [ ] **Step 5: Route every visibility transition through the helpers**

In `Update()`, replace the F10 open body (`_visible = true;` + `LogInfo("overlay opened")`) with `OpenOverlay();`. In `OnGUI`'s outer catch, replace `_visible = false;` with `CloseOverlay();` (keep the existing `overlay draw failed` warning line after it). The F10-close branch already calls `CloseOverlay()`.

- [ ] **Step 6: Sync the user-guide**

In `docs/user-guide.md` §3, after "every config key as a live control." add: "While it is open the overlay **blocks clicks to game UI behind it**; closing it restores normal game input."

- [ ] **Step 7: Run Cycle T1–T4 with Review Focus 4 + 5**

Expected: with the overlay open, clicking a game button behind it does nothing; closing restores game buttons. Review Focus 4: temporarily change the lookup string to `"GFXConf.DoesNotExist"`, rebuild, open → one `type not found` warning, overlay still works; restore the real name. Review Focus 5: the `OnGUI` catch path calls `CloseOverlay()` so the EventSystem is never left disabled. T3: no `Unhandled exception`.

- [ ] **Step 8: Commit**

```powershell
git add src/GFXConf/Overlay.cs docs/user-guide.md
git commit -m "feat(overlay): block game input while open; centralize open/close"
```

---

### Task 3: Draggable title bar + snap-back on open

**Files:**
- Modify: `src/GFXConf/Overlay.cs`
- Modify: `docs/user-guide.md`

**Interfaces:**
- Consumes: `OpenOverlay()`/`CloseOverlay()` (Task 2).
- Produces: `_panelPos : Vector2`, `_dragging : bool`, `_dragOffset : Vector2`, `HandleHeaderDrag()`.

- [ ] **Step 1: Add drag state**

Seeded from the existing `PanelRect` default:

```csharp
private static Vector2 _panelPos = new(PanelRect.x, PanelRect.y);
private static bool _dragging;
private static Vector2 _dragOffset;
```

- [ ] **Step 2: Add `HandleHeaderDrag()`**

Handle the current IMGUI event against the header, excluding the close-X rect:

```csharp
private static void HandleHeaderDrag()
{
    var e = Event.current;
    if (e == null) return;

    var panel = ClampToScreen(new Rect(
        _panelPos.x, _panelPos.y, PanelRect.width, PanelRect.height));
    var header = new Rect(panel.x, panel.y, panel.width, HeaderHeight);
    var close = new Rect(header.xMax - 24f, header.y + 4f, 20f, HeaderHeight - 8f);
    var mouse = e.mousePosition;

    if (e.type == EventType.MouseDown && e.button == 0
        && header.Contains(mouse) && !close.Contains(mouse))
    {
        _dragging = true;
        _dragOffset = mouse - new Vector2(panel.x, panel.y);
        e.Use();
    }
    else if (e.type == EventType.MouseDrag && _dragging)
    {
        var clamped = ClampToScreen(new Rect(
            mouse.x - _dragOffset.x, mouse.y - _dragOffset.y,
            PanelRect.width, PanelRect.height));
        _panelPos = new Vector2(clamped.x, clamped.y);
        e.Use();
    }
    else if (e.type == EventType.MouseUp)
    {
        _dragging = false;
    }
}
```

- [ ] **Step 3: Drive `DrawPanel` from `_panelPos` and call the drag handler**

At the top of `DrawPanel`, call `HandleHeaderDrag();`, then build the panel from `_panelPos`:

```csharp
var panel = ClampToScreen(new Rect(
    _panelPos.x, _panelPos.y, PanelRect.width, PanelRect.height));
```

The rest of `DrawPanel` (header, scroll, `FillOpaque`, `GUI.Box`, close X) is unchanged.

- [ ] **Step 4: Reset on open / clear on close**

In `OpenOverlay()` (before `_visible = true`), reset:

```csharp
_panelPos = new Vector2(PanelRect.x, PanelRect.y);
_dragging = false;
_windowRect = new Rect(20f, 20f, 360f, 520f);
```

In `CloseOverlay()`, add `_dragging = false;`.

- [ ] **Step 5: Sync the user-guide**

In `docs/user-guide.md` §3, add: "The overlay can be **dragged by its title bar**; it snaps back to the top-left corner every time it is reopened."

- [ ] **Step 6: Run Cycle T1–T4 with Review Focus 1–3**

Expected: dragging the title bar moves the panel and releasing keeps it; Review Focus 1: clicking the X closes without dragging; Review Focus 2: dragging past a screen edge clamps the panel fully on-screen; Review Focus 3: close + reopen puts it back at the default top-left. SCPE/PPv2 toggles and scrolling still work. T3: no `Unhandled exception`.

- [ ] **Step 7: Commit**

```powershell
git add src/GFXConf/Overlay.cs docs/user-guide.md
git commit -m "feat(overlay): drag by title bar with snap-back on open"
```

---

## Final Verification (after Task 3)

- [ ] **Step 1: Full clean run with evidence**

Launch once, exercise: open overlay → drag → close X → reopen (default corner) → toggle a setting live → click a game button behind the open overlay (must not activate) → close (game input back). Collect `Select-String -Pattern "GFXConf|Unhandled exception"` evidence.

- [ ] **Step 2: Confirm no regression**

`gfxconf.cfg` is unchanged (no new/renamed keys); a normal startup still logs the banner, `scene=…` sweep lines, and the pinned summary line; zero `Unhandled exception` and zero GFXConf `LogError`.

- [ ] **Step 3: Present for review**

Present all branch commits (message + files + diff summary) to the user; **push only after explicit approval.**
