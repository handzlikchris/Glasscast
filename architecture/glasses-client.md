# The glasses client (client-web)

A React 19 + TypeScript app built by Vite into `client-web/dist`, which the server serves
from disk. It runs in the Meta Ray-Ban Display WebView: 600×600, additive display (black is
transparent), input as arrow keys (swipes), pointer taps at the glasses' pointer (pinches),
pointer drags (pinch-drag) and `history.back()` (middle-finger pinch).

## Files

| File | Role |
| --- | --- |
| `src/main.tsx` | Mounts `<App/>` without `<StrictMode>` (double effects would open two sessions). |
| `src/App.tsx` | Phases: `choose` (PC or Phone, last choice focused) → `pairing` (skipped with a stored device token) → `session` → `ended` (Reconnect / Pair again / PC or phone). End on a bar goes straight back to `choose`. |
| `src/PairingScreen.tsx` | Shows the pairing code and countdown; Try again. |
| `src/pinchPress.ts` | Outside a session and on a phone session's bar and Type panel, a pinch anywhere presses the focused button, or focuses and clicks the focused text box (which opens the composer). |
| `src/connection.ts` | Pair and session sockets; the only place tokens live (see [pairing-and-auth.md](pairing-and-auth.md)). |
| `src/rtc.ts` | Receive-only `RTCPeerConnection` (video to `<video>`, the audio stream to `AudioOutput`), stats snapshot, `watchFrames`. |
| `src/audioOutput.ts` | Plays the PC's sound through Web Audio (a muted `<audio>` keeps the stream flowing); see [audio.md](audio.md). |
| `src/SessionScreen.tsx` | The session UI and all its behaviour (~1000 lines): connection effect, navigation, gestures, modes, panning, overlay, toolbar. |
| `src/TypePanel.tsx` | Text box for the composer, Send text / Clear, shortcut keys, the focus chain. |
| `src/focusnav.ts` | Pure navigation model: `NavTarget` (`view`/`controls`), `routeTap`, `backTarget`, `menuFocusFor`, `nextAppSlot`. |
| `src/swipes.ts` | **The one place swipes on the view are decided**, for PC and phone sessions: `SwipeReader` (single vs double left/right, `DOUBLE_SWIPE_MS` 300), `pcSwipeAction`, `phoneSwipeAction`, `waitingHint`. See "Swipes" below. |
| `src/PhoneScreen.tsx` | A phone session (see [phone-mode.md](phone-mode.md)). |
| `src/gestures.ts` | `GestureTracker` (tap vs drag, 10 px / 500 ms), `TapThenHold`, `DOUBLE_TAP_MS` 350, `HOLD_MS` 500. |
| `src/controls.ts`, `src/geometry.ts` | Cursor/pan/scroll maths and letterbox geometry (see [input-and-desktop.md](input-and-desktop.md)). |
| `src/overlay.ts` | Canvas: cursor (white or high-contrast yellow), region box, pan-edge glow. |
| `src/display.ts`, `src/scrollPrefs.ts` | Brightness levels and per-app scroll strength, in localStorage. |
| `src/audio.ts` | The ♪ setting (localStorage, default on), the stereo fix-up of the answer, the status bar's `V n · A n kbps` label (see [audio.md](audio.md)). |
| `src/mediaStats.ts` | Latency matching and the Stats panel text (see [stats-and-diagnostics.md](stats-and-diagnostics.md)). |
| `src/styles.css` | Looks (`natural`, `lifted`, `contrast`), `--brightness`, toolbar, panels; `touch-action: none` must stay in the initial CSS. |
| `build-label.mjs` | `Build <commit>[+] · <date time>` baked in as `__BUILD__`, shown on pairing/ended screens. |

## SessionScreen structure

State that must not go stale inside event handlers is mirrored in refs (`live`, `focused`,
`pin`, `currentApp`, `activeAppRef`, `scrollLevelsRef`, `edgeScroll`, …); handlers are
reassigned to refs each render (`swipeRef`, `backRef`, `setNavRef`, `restorePinRef`).

Effects:
1. **Connection** (mount once): `Session.open`, `VideoReceiver`, `watchFrames`, ping every
   2 s, stats every 1 s, scroll flush every 50 ms, visibility watch (hidden 5 s → end). Cleanup
   closes everything.
2. **Navigation** (mount once): `focusin`, captured `pointerdown`/`pointerup`/`click` on the
   stage (a pinch while on the controls presses the *focused* control), `keydown` on the
   document (swipes, Escape/Backspace as Back, Enter de-duplication).
3. **History**: pushes one entry so Back arrives as `popstate`; re-pushed after each Back.
4. **Overlay** redraw when cursor, region box, look or edge glow change.

## Navigation model

- `nav = 'view'`: swipes act on the desktop (`swipes.ts`, see "Swipes"), the top bar is hidden
  and click-through.
- The status bar says `live (local)` or `live (remote)`: `mediaPath` on the PC's address in the
  chosen ICE pair (private = local; see the LAN path in media-pipeline.md). Next to it
  `V n · A n kbps` (payload received; `A off` with ♪ off); the codec only when it isn't H.264. `nav = 'controls'`: swipes move focus, a pinch presses the focused control.
- Back from the view → controls (focus on Type from Pointer, Pointer otherwise); Back from the
  controls (and so from Type and Region) → home to Pointer mode. Two Backs within 400 ms count
  once (`SAME_BACK_MS`); a tap and an Enter within 500 ms are the same pinch.
- The glasses reset focus after a Back and when the composer closes. Focus the app moves on
  purpose goes through `focusPinned`, which restores it for 600 ms (re-checked at 50/150/300/500 ms);
  any swipe or pinch ends the pin.

## Swipes (interface decision, 2026-09-27)

One rule set for PC and phone sessions, in `src/swipes.ts` only; the screens feed arrow keys to
a `SwipeReader` and act on what `pcSwipeAction` / `phoneSwipeAction` return.

| Swipe on the view | PC session | Phone session |
| --- | --- | --- |
| up / down | at once: scroll (Pointer mode), pan (Pan on, View, Scroll) | at once: scroll the phone around the cursor |
| right, right (within 0.3 s) | **Type** | **Type** |
| left, left (within 0.3 s) | **next app** shortcut | the phone's **Back** |
| right or left once | after 0.3 s: nothing in Pointer mode, pan with Pan on / View / Scroll | after 0.3 s: page (a sideways swipe on the phone) |

- Why doubles: the shortcuts are the same on both targets, and a single stray swipe can't open
  Type or switch apps. The price: a single left or right acts 0.3 s late (accepted by the user; 0.5 s at first, cut to 0.3 s the same day as quick enough for a double).
- While a left/right waits, the status bar says what the second one would do ("swipe right
  again for Type").
- An up or down swipe while a left/right waits drops the waiting one: the band reads some
  down-swipes as left, and that stray left must not act.
- A waiting swipe that lands after the swipes left the view (Back, a panel opened) is ignored.
- Swipes on the controls, in Type and in Region don't go through this: they move focus (or zoom
  the Region box).

## Type flow

Entering Type (`flushSync` so the panel exists inside the same user gesture) focuses and
clicks the textarea to try to open the composer → the composer's `change` moves focus to
**Send text** → sending moves focus to **Enter** → Enter returns to Pointer mode. A swipe of
the user's breaks the chain. Text has no length limit: `textChunks` sends it as `typeText`
messages of at most 500 characters, split between two non-space characters (the server trims
each message) and never inside an emoji; the server flattens newlines.

## Local storage

Only `glassesRemote.device` (device token + expiry), `glasses.videoBrightness`,
`glasses.scrollLevels`, `glasses.audio` (`on`/`off`). Every access is wrapped in try/catch; blocked storage just means
nothing is remembered.

## Build and deploy

`npm run build` = `tsc --noEmit && vite build`. The running server serves the new `dist`
at once; the user presses Restart in the glasses' web app menu. `index.html` is `no-cache`,
hashed `/assets` are immutable (`ClientCaching`). Strict CSP: no inline scripts/styles in
`index.html`; React `style` props are fine. Budget: <300 KB first load, <15 requests.

## Tests

Unit (Vitest, node): `focusnav`, `gestures`, `controls`, `geometry`, `protocol`,
`mediaStats`, `display`, `scrollPrefs`. `SessionScreen`, `TypePanel` and `connection` have no
unit tests; they are covered by the e2e harness (`tools/e2e-harness/browser/drive.mjs`).
