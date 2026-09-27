# Input, modes and the desktop

How gestures on the glasses become mouse, wheel and keyboard input on the PC, which part of
the monitor is shown, and how app shortcuts bring windows into view.

## Files

| File | Role |
| --- | --- |
| `server/Sessions/InputController.cs` | Per-session mode + region; applies messages only in their mode; keeps the cursor inside the region for clicks and wheel. |
| `server/Desktop/RegionMath.cs` | `CaptureRegion`, `PixelSize/Rect`; `Clamp` (on monitor, ≥160 px), `Default` (centred square, half the short side), `ToScreen` (0..1 → pixel), `Fit` (letterbox). |
| `server/Desktop/RegionStore.cs` | Last region in `%LOCALAPPDATA%\GlassesRemote\region.json` (`Desktop:RegionFile`); loaded at session start, saved on every `setRegion`. |
| `server/Desktop/CastArea.cs` | The active session's region (null when none), with a `Changed` event for the tray's frame. |
| `server/Desktop/Abstractions.cs` | `IScreen`, `ICaptureSource`, `IInputInjector`, `IKeepAwake`. |
| `server/Desktop/AppShortcuts.cs` | `AppShortcut` (Name ≤16, Process and/or Title), `AppShortcutOptions.Usable` (valid, max 9), `IWindowSwitcher`, `AppSwitchResult`. |
| `server/Windows/Win32InputInjector.cs` | `SendInput`: absolute moves over the virtual desktop, left click, wheel, `KEYEVENTF_UNICODE` text, key chords. |
| `server/Windows/Win32WindowSwitcher.cs` | EnumWindows (Z-order = most recent first), Alt+Tab-style filter, restore, fit the visible frame (DWM extended bounds) to the cast area, bring to front (AttachThreadInput fallback). |
| `server/Windows/WindowsDesktop.cs` | `WindowsScreen` (primary monitor, physical px), `WindowsKeepAwake` (SetThreadExecutionState on a dedicated thread). |
| `client-web/src/geometry.ts` | Mirror of `RegionMath` (`fit`, `clampRegion`, `contentRect`) plus overview/region-box math. |
| `client-web/src/controls.ts` | Cursor movement (locked or with edge panning), edge-scroll state machine, `toNormalized`, `ScrollAccumulator`, `nudgeRegion`. |
| `client-web/src/scrollPrefs.ts` | Scroll strength per app name (9/5/3/2/1 notches, default 3). |

## Modes (server-gated)

| Mode | Button | Video source | Accepted input |
| --- | --- | --- | --- |
| `overview` | **Region** | whole monitor | `setRegion` (the draft box is client-side until **Use region**) |
| `pointer` (start) | **Pointer** | region | `move`, `click`, `scroll` |
| `type` | **Type** | region | `typeText`, `key` |
| `view`, `scroll` | none any more | region | nothing / `scroll` |

`setMode` and `setRegion` are accepted in any mode. Everything else in the wrong mode is
ignored (`HandleResult.IgnoredForMode`), so a pinch-drag can't mean two things.

## Coordinates

- The process is PerMonitorV2 DPI-aware (`ApplicationConfiguration.Initialize()` first in
  `Main`), so capture, `SendInput` and `SetWindowPos` all use physical pixels of the
  **primary** monitor.
- Frames are always 600×600 with the source letterboxed (`Fit`). The client's `contentRect`
  mirrors it; a `move` is the cursor position as 0..1 inside that content rect, and
  `RegionMath.ToScreen` maps it into the region.
- `Win32InputInjector.MoveTo` normalises to 0..65535 across the virtual desktop
  (`MOUSEEVENTF_VIRTUALDESK`), so other monitors don't shift the target.
- The client draws its own cursor (GDI capture has none) and keeps it in `cursor` state;
  the server remembers the last position it moved to (`_cursor`) and moves to the region
  centre before a click or wheel if that position is outside the region.

## Pointer behaviour (client, `SessionScreen` + `controls.ts`)

- Pinch-drag → `moveCursorLocked` (Pan off, default): the cursor reaches the edges; pushing
  past top/bottom starts hold-to-scroll (`edgeScrollStep`), also when the glasses' own
  pointer is pinned against the display edge. Scrolling runs on the 50 ms flush timer at
  half a swipe's worth per second until the drag ends or comes back 12 px.
- Pan on → `moveCursorWithEdgePan`: inside a 24 px edge zone, the rest of the push slides the
  region (`setRegion` at most every 100 ms; local copy wins until all are answered).
- Pinch → click after 350 ms unless a second pinch follows: released quickly, a double-click;
  moved or held, tap-and-a-half: the left button held (`mouseButton`) until it ends (see
  glasses-client.md "Pinches").
- Swipes: see glasses-client.md "Swipes" (`swipes.ts`): up/down scroll by the app's level, right
  twice → Type, left twice → next app; single left/right pan with Pan on.

## Typing

`typeText` sends each UTF-16 unit with `KEYEVENTF_UNICODE` (layout-independent), in chunks of
100 characters. Text never contains line breaks (flattened in the parser); Enter is only ever
the separate `key` message. Keys are a fixed allowlist mapped to virtual-key chords.

## App shortcuts

`Apps:Shortcuts` in `appsettings.Local.json` (PC only; read at startup). The glasses get the
names in `hello` and send a slot number. `Win32WindowSwitcher.Switch` finds the first visible,
titled, unowned, non-tool, non-cloaked top-level window whose title contains `Title` and whose
process is `Process`, restores it if minimised/maximised, sizes its *visible* frame onto the
cast area and brings it to the front. Result `switched` / `notRunning` / `failed` goes back as
`appSwitch`; the client only moves its highlight on `switched`. Never launches anything.
Console apps started from Run live in `WindowsTerminal` with no process link, so match those
by the title the app sets.

## Rules

- UIPI: input to elevated windows is blocked by Windows; that's accepted.
- The e2e harness records input and switches; it must never move real windows.
- Keep `RegionMath.cs` ⇄ `geometry.ts` in sync (min size 160, letterbox rounding).

## Tests

`tests/Sessions/InputControllerTests.cs`, `tests/Desktop/RegionMathTests.cs`,
`tests/Desktop/CastAreaTests.cs`, `EndpointTests` (pointer mapping, clamped regions, app
shortcuts), `client-web/src/{geometry,controls,scrollPrefs}.test.ts`, and most e2e checks.
