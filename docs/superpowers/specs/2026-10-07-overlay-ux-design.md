# GFXConf Overlay UX Improvements — Design

Date: 2026-10-07
Status: approved by user 2026-10-07 (implementation may proceed)
Scope path: bounded UI change to the existing F10 overlay (`Overlay.cs`)
Branch: `feat/overlay-ux`

Follows the base design in
`docs/superpowers/specs/2026-10-07-gfxconf-design.md`; this document only
covers the overlay UX changes. Nothing here alters the config schema
(`gfxconf.cfg` §4.2 of the base spec) or the sweep behavior.

## 1. Goal

Make the in-game F10 overlay easier and less confusing to use, without
changing what the plugin disables:

1. The overlay can be **dragged around the screen by its title bar**.
2. The overlay **snaps back to its default top-left position** every time
   it is opened (session-only position — never persisted).
3. The **footer is removed** — neither the `F10 = close` hint nor the
   last-sweep summary is shown. Sweep detail remains available in
   `BepInEx\LogOutput.log` and the BepInEx console.
4. Toggle rows show the **config key only** (e.g. `DisableAmbientOcclusion`);
   the trailing `= true`/`= false` copy is removed. The checkbox is the
   only state indicator.
5. `OverrideMode` and `DelaySeconds` **keep showing their values** — unlike
   a checkbox they have no other on-screen indicator.
6. **Clicks no longer pass through** the overlay to game UI behind it.

## 2. Current behavior (context)

`Overlay.cs` owns all of this. Relevant facts:

- The overlay is drawn with IMGUI in `GfxBehaviour.OnGUI`.
- Two draw paths exist: `GUILayout.Window` (contract-pinned first attempt)
  and the fixed panel (`DrawPanel`). On this game build the Window path is
  **dead** — `LayoutedWindow` was stripped from the IL2CPP metadata, so the
  first call throws `MissingMethodException` and `_panelMode` latches to the
  panel path permanently (verified in `LogOutput.log`). All effective work
  happens in the panel path.
- The panel position is the static readonly `PanelRect` (`16,16,360,620`),
  clamped every draw by `ClampToScreen`.
- `DrawFooter` renders the `F10 = close` hint and `Sweeper.LastSummary`
  inside a fixed `FooterHeight` (80 px) strip.
- `DrawToggle` labels each row `"{key} = {value}"`.
- Click-through: the game ships `UnityEngine.UI.dll` with the
  `UnityEngine.EventSystems` namespace (uGUI). IMGUI drawing does not block
  the uGUI `EventSystem`, so clicks on the overlay also hit game buttons.

## 3. Scope

### In scope
- Title-bar dragging of the fixed panel; session-only position.
- Snap-back to default on every open.
- Footer removal (hint + summary).
- Toggle label = key only.
- Disable the game `EventSystem` while the overlay is open; restore on close.
- Removal of the now-unused `Sweeper.LastSummary`.

### Out of scope (explicitly dropped)
- Configurable overlay shortcut key (default stays **F10**).
- Persisting overlay position across sessions (no new config key).
- Resize handle, drag grip, panel-size changes.
- Any change to the sweep, the config schema, or the log line formats.

## 4. Design

### 4.1 Dragging (title bar only)

- Replace the fixed `PanelRect` origin with a mutable static
  `_panelPos` (a `Vector2`), seeded from `PanelRect`'s default. The panel
  rect each draw becomes `ClampToScreen(new Rect(_panelPos.x, _panelPos.y,
  PanelRect.width, PanelRect.height))`.
- New static drag state: `_dragging` (bool) and `_dragOffset` (Vector2).
- In the panel path, handle the current IMGUI event against the **header
  rect** (`DrawPanel` already computes it):
  - `MouseDown` (button 0) inside the header **and not inside the close‑X
    rect** → `_dragging = true`, `_dragOffset = mouse - _panelPos`.
  - `MouseDrag` while `_dragging` → `_panelPos = mouse - _dragOffset`,
    clamp via `ClampToScreen`, then `Event.current.Use()` so the drag
    doesn't leak into scrolling.
  - `MouseUp` → `_dragging = false`.
- The close‑X rect is excluded from the drag region so a click on X never
  starts (or continues) a drag. Because `_dragging` only starts inside the
  header, the scroll view and toggles are unaffected.
- `_dragOffset` is captured from `Event.current.mousePosition`, which is in
  the same GUI space as the rects (top-left origin).

### 4.2 Snap-back on open

- The open path resets `_panelPos = new Vector2(PanelRect.x, PanelRect.y)`
  and `_dragging = false`. Opening is centralized (see §4.4), so F10 and
  any future open path get the same reset.
- Position is never written to config; restarting the game also starts at
  the default.

### 4.3 Footer removal + label change

- Remove the footer rect, `DrawFooter`, `FooterHeight`, and `_wrapStyle`
  from both draw paths. The scroll area extends into the space the footer
  occupied; the panel keeps its `360x620` default size (minimal change).
- `DrawToggle` label becomes `entry.Definition.Key` only; the checkbox
  still shows the value. `DrawOverrideMode` and `DrawDelaySeconds` are
  unchanged (they keep `OverrideMode = <value>` / `DelaySeconds = <n>`).

### 4.4 Click-through prevention

- Resolve `UnityEngine.EventSystems.EventSystem` by name across loaded
  assemblies (defensive, cached — same pattern as `Sweeper.ResolveType`).
  Missing type → one `[GFXConf] type not found: ...` warning, then skip.
- On open: `Object.FindObjectsOfType(Il2CppType.From(type))`, re-wrap each
  result as a `Behaviour` (interop wrapper-cast caveat, as in
  `Sweep.DisableComponents`), set `enabled = false`, and keep a strong
  reference in a static list.
- On close: set each referenced `Behaviour.enabled = true` and clear the
  list.
- Centralize visibility transitions into two helpers — `OpenOverlay()` and
  `CloseOverlay()` — so **every** path restores game input:
  - F10 opens → `OpenOverlay()`: `_visible = true`, reset position/drag
    state (§4.2), block input (§4.4), log `[GFXConf] overlay opened`.
  - F10 closes, the X button, and the `OnGUI` draw‑failure catch all call
    `CloseOverlay()`: `_visible = false`, unblock input, log
    `[GFXConf] overlay closed`. (The draw‑failure path additionally logs
    its own warning, as today.)
- Trade-off (accepted): while the overlay is open the game's UI cannot be
  clicked at all; this is the intended modal behavior for a settings panel,
  and the overlay's own IMGUI controls are unaffected.

### 4.5 Cleanup

- `Sweeper.LastSummary` is only read by the removed footer → delete the
  property and its assignment. The `LogInfo(summary)` summary line is
  unchanged.

## 5. Error handling

- Every game-state touch (EventSystem type lookup, disable, restore) runs
  in its own try/catch → `LogWarning`, never rethrow (project rule 2).
- Failures latch to at most one warning per session for the type lookup,
  matching the existing "log once" convention; no per-frame warnings.
- If the EventSystem type is absent or no EventSystems exist, the overlay
  still opens and closes normally.

## 6. Files touched

| File | Change |
|---|---|
| `src/GFXConf/Overlay.cs` | Drag state + handling, snap-back, footer removal, `DrawToggle` label, `OpenOverlay`/`CloseOverlay`, EventSystem block/restore, remove `DrawFooter`/`FooterHeight`/`_wrapStyle` |
| `src/GFXConf/Sweep.cs` | Remove unused `LastSummary` |
| `docs/user-guide.md` | Update §3 overlay description (draggable, no footer, no click-through, snap-back) |
| `docs/superpowers/specs/2026-10-07-overlay-ux-design.md` | This spec |

No change to `gfxconf.cfg` keys, defaults, or log line formats that the
user guide documents as pinned.

## 7. Testing plan

1. **Build:** `dotnet build -c Release` succeeds; deploy `GFXConf.dll` to
   `BepInEx\plugins\`.
2. **Drag:** F10 → overlay opens at top-left; dragging the title bar moves
   it; releasing keeps the new spot; the panel stays clamped on-screen.
3. **Snap-back:** close (F10 or X) and reopen → overlay is back at the
   default top-left; the close X still closes without dragging.
4. **Footer/labels:** no footer or sweep summary is drawn; each toggle row
   shows only its key name; `OverrideMode`/`DelaySeconds` still show values.
5. **Click-through:** with the overlay open, a game button behind the panel
   does **not** activate; after closing, game buttons work again.
6. **Log triage:** `LogOutput.log` shows the banner, `overlay opened` /
   `overlay closed`, and **no** `Unhandled exception` and no `LogError`
   from GFXConf.
7. **Crash safety:** with the EventSystem type forced missing (or no
   EventSystem present), the overlay still opens/closes and logs one
   warning — the game keeps running.

## 8. Acceptance criteria

- The overlay is draggable by the title bar and snaps to the default
  top-left position on every open; restarting also starts at default.
- No footer and no trailing `= true/false` copy appear.
- Clicking through the overlay to game UI is prevented while it is open
  and game input is restored on close.
- No config schema change; no new crash surface; `LogOutput.log` is clean.
