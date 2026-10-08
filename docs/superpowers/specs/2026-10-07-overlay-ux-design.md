# GFXConf Overlay UX Improvements — Design

Date: 2026-10-07
Status: approved 2026-10-07; amended 2026-10-08 after runtime testing
Scope path: bounded UI change to the existing F10 overlay (`Overlay.cs`)
Branch: `feat/overlay-ux`

Follows the base design in
`docs/superpowers/specs/2026-10-07-gfxconf-design.md`; this document only
covers the overlay UX changes. Nothing here alters the config schema
(`gfxconf.cfg` §4.2 of the base spec) or the sweep behavior.

**2026-10-08 amendment:** Runtime testing showed that disabling the game's
input system while the overlay was open caused repeated
`NullReferenceException`s. That behavior and its restore path have been
removed; game input is now left untouched while the overlay is visible.

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
6. Game input is left untouched while the overlay is open; clicks may reach
   game UI behind it.

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
- The IMGUI panel does not block game input, so clicks may reach game UI
  behind it. The overlay must not disable or otherwise modify game input
  systems.

## 3. Scope

### In scope
- Title-bar dragging of the fixed panel; session-only position.
- Snap-back to default on every open.
- Footer removal (hint + summary).
- Toggle label = key only.
- Leave game input systems enabled while the overlay is open. Click-through
  prevention is deferred until a safe approach is separately designed and
  tested.
- Removal of the now-unused `Sweeper.LastSummary`.

### Out of scope (explicitly dropped)
- Configurable overlay shortcut key (default stays **F10**).
- Persisting overlay position across sessions (no new config key).
- Resize handle, drag grip, panel-size changes.
- Any change to the sweep, the config schema, or the log line formats.
- A modal input shield or any other click-through prevention mechanism.

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
  and `_dragging = false`. Opening is centralized in `OpenOverlay()`, so F10 and
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

### 4.4 Input behavior

- Do not look up, disable, or otherwise mutate game input systems when
  opening or closing the overlay. Runtime testing showed disabling the
  game's input system caused repeated `NullReferenceException`s while the
  overlay was open; the errors stopped when it closed.
- Game input remains active while the overlay is visible. Clicks may reach
  game UI behind the panel; this is accepted for now in favor of stability.
- Any future click-through prevention must use a separately designed and
  tested approach that leaves the game's input system enabled.

### 4.5 Cleanup

- `Sweeper.LastSummary` is only read by the removed footer → delete the
  property and its assignment. The `LogInfo(summary)` summary line is
  unchanged.

## 5. Error handling

- Overlay drawing failures are caught and close the overlay without changing
  game input state.

## 6. Files touched

| File | Change |
|---|---|
| `src/GFXConf/Overlay.cs` | Drag state + handling, snap-back, footer removal, `DrawToggle` label, `OpenOverlay`/`CloseOverlay`; remove EventSystem input blocking and restore code |
| `src/GFXConf/Sweep.cs` | Remove unused `LastSummary` |
| `docs/user-guide.md` | Update §3 overlay description (draggable, no footer, click-through possible, snap-back) |
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
5. **Input behavior:** game input remains enabled while the overlay is open;
   clicks may reach game UI behind the panel. No input-block/restore messages
   are logged.
6. **Log triage:** `LogOutput.log` shows the banner, `overlay opened` /
   `overlay closed`, and **no** `Unhandled exception` and no `LogError`
   from GFXConf.
7. **Crash safety:** open and close the overlay repeatedly; verify no NREs
   occur from overlay input handling and the game keeps running.

## 8. Acceptance criteria

- The overlay is draggable by the title bar and snaps to the default
  top-left position on every open; restarting also starts at default.
- No footer and no trailing `= true/false` copy appear.
- Game input systems are not modified while the overlay is open; click-through
  may occur.
- No config schema change; no new crash surface; `LogOutput.log` is clean.
