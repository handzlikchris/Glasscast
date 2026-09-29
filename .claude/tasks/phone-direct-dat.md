# Task: phone-direct POC — the phone drives the glasses itself (no PC, no internet)

Status: **plan, nothing built**. Written 2026-09-29 after the user's brainstorm. Read `CLAUDE.md`,
then `architecture/phone-mode.md` (the current phone mode, whose Android code this reuses).

## Why

Most of the user's work on the go will be **phone apps driven from the glasses** (e.g. the
Claude mobile app), not a cast PC desktop: phone apps are already built for small screens.
Phone mode today still needs the PC: it serves the web app, pairs the glasses and relays the
signalling. So a dead spot on mobile data ends the session, even though the video and input
already go straight from phone to glasses. The goal: **glasses ↔ phone directly, no internet at
all**. When the phone is online, its apps use the internet themselves.

## The idea: a native display session instead of the web app

Meta's **Wearables Device Access Toolkit (DAT) 1.0** (Maven Central, 2026-09-24) lets a phone
app talk to the glasses over Bluetooth LE (plus Wi-Fi for high-bandwidth features):

- **`mwdat-display`** (since 0.7): the phone sends a declarative screen (`sendContent`: FlexBox,
  text, buttons, icons, **`image(bitmap = …)`**, an MP4 player). Each call **replaces the whole
  screen**; no partial updates. 600×600. Docs warn that big images lag on Bluetooth bandwidth.
  The display dims after 20 s without activity and sleeps at 25 s (the session survives). Back (two-finger tap on
  the temple) ends the display session. Video: MP4 from an **https URL only**, ≤ 400 px per side,
  ≤ 70,000 px, one per session, so it can't carry a live stream.
- **`mwdat-inputs`** (1.0): `InputEvent` subclasses `Nav` (direction), `Select`, `Back`,
  `Button`, `Capture`, `Drag` (**`x`, `y`, `dx`, `dy`**, continuous pinch-and-drag). That is
  nearly the web app's gesture set: swipes, pinch, pinch-drag, Back.
- **`mwdat-speech`** (1.0): on-device transcription (`TranscriptionResult`), mic permission.
  A replacement for the web app's composer in Type.
- One DAT session per device at a time. Needs the Meta AI app with **Developer Mode** (tap the
  version five times) to run an unpublished app; sideload with adb.

So the phone app becomes the whole product: it captures its own screen, draws each 600×600 frame
(picture, cursor, mode bar, status) into **one bitmap**, sends it with `sendContent`, and turns
`Inputs` events into taps, swipes and text through the existing accessibility service. No sockets,
no certificates, no PC, no pairing popup: only Meta's own glasses-to-phone link.

```
phone screen ─MediaProjection─► crop/scale ─► compose (cursor, bar) ─► Bitmap ─DAT Display (BLE)─► glasses
accessibility ◄── tap/swipe/nav/text ◄── gesture rules ◄── DAT Inputs (Nav/Select/Drag/Back) ◄── Neural Band
                                     ◄── DAT Speech (dictation for Type)
```

### Why not the web app offline

Kept as the fallback (below), but each step is an unknown: the glasses load only HTTPS pages from
a public URL (Meta's docs, which say nothing about offline, service workers, or reaching the
phone); a page can't open `ws://` to the phone (mixed content); a name for the phone needs DNS,
which is gone offline; and WebRTC without a signalling server means hand-made SDP (fixed ICE
credentials and persistent DTLS certificates on both sides). DAT sidesteps all of it.

### Costs of the native path (be honest with the user)

- **Frame rate is the big unknown.** Whole-screen bitmaps over Bluetooth LE: a 600×600 phone
  screenshot is tens of KB, so expect a few frames a second, not 20-30. Fine for reading and
  tapping in Claude; poor for scrolling. N0 measures it before anything else is built.
- **Cursor lag:** the cursor is drawn into the frame on the phone, so it moves at the frame rate
  (the web app draws it locally). Mitigation: send a frame at once on cursor moves, with the
  cursor, and redraw from the last captured picture instead of waiting for a new capture.
- **Dev loop:** a Kotlin rebuild + wireless `adb install` instead of `npm run build` + Restart.
- **Gesture rules exist in TypeScript** (`swipes.ts`, `focusnav.ts`, the tap-and-a-half logic
  in `PhoneScreen.tsx`). They get ported to Kotlin with the same test tables; two copies to keep
  in step while both clients live.
- DAT is a developer preview: install on own devices only, APIs may change.

## Reuse from `android-companion/`

Same APK, a second mode (**Direct**) next to the PC-relayed one: one accessibility service, one
set of Android permissions, the same Region/Fit and app switching.

| Keep | New |
| --- | --- |
| `InputService` (gestures, global actions, text as IME, keep screen on), app switcher, Fit (window bounds) | `direct/DatSession` (DAT session, display, inputs, speech; thermal and device state) |
| `ConsentActivity` (MediaProjection consent, every session) | `direct/FrameSource` (MediaProjection → `ImageReader` instead of libwebrtc; crop to region; change detection) |
| `InputProtocol`'s message shapes as internal commands | `direct/Composer` (draws picture + cursor + bar + status into the 600×600 bitmap) |
| `CropProcessor`'s region maths | `direct/Gestures` (Kotlin port of `swipes.ts`, pinch/tap-and-a-half, drag gain), `direct/Controls` (bar focus, drawn in-frame) |

DAT's sample needs `minSdk 31` and `compileSdk 36`; the companion is on 30 / 35. Bump both
(the S25 runs Android 16). Android platform 36 isn't installed on this PC: **ask before installing**.

## Milestones

| # | What | Done when |
| --- | --- | --- |
| **N0** | **Spike on the S25** (throwaway activity in the companion): DAT session; show a bitmap; loop `sendContent` with a numbered 600×600 test card and with real screenshots at JPEG-ish detail. Log every `Inputs` event. Then again in **airplane mode with Bluetooth on**. | Written down: frames/s and time per `sendContent` for 3 image sizes; whether updates stop the 20 s dim; whether `Inputs` events arrive when the screen is just an image; `Drag`'s coordinate space and rate; `Nav` directions; whether `Back` can be kept in the app or always ends the display; Speech works offline; all of it works with no internet. **Go/no-go with the user.** |
| N1 | Mirror: consent → capture → crop (Region/Fit) → compose → send; only send when the picture or cursor changed; adapt the frame size to what the link carries | Claude app readable on the glasses, frame rate shown in a corner |
| N2 | Input: Drag → cursor, Select → tap / double tap, tap-and-a-half → touch hold, Nav → the `swipes.ts` table (scroll, page, Back, previous/next app), Back → controls | The same gestures as a phone session today |
| N3 | Controls: Back · Home · Apps · Notif · Type · Region · Fit · End drawn in the frame, focus moved by Nav, pressed by Select | Bar usable without touching the phone |
| N4 | Type: Speech dictation → preview in-frame → **Send text** → **Enter** (same steps as `TypePanel`); text goes through the IME path | Prompt typed into the Claude app by voice, offline |
| N5 | On the go: keep screen on, lock handling, thermal (`ThermalLevel`), battery, a stats line (fps, send ms) in a log file like the PC's stats log | An hour's use on the move, figures logged |

## Fallback if N0 says no (frame rate too low or inputs unusable)

Keep the web app and make phone mode survive losing the internet, in this order of cost:
1. **Cheap test with today's code:** start a phone session at home, then turn the phone's Wi-Fi
   and mobile data off. If the video and input keep going, the phone↔glasses path is local and
   only *starting* a session needs the PC.
2. Cache the web app in a service worker (if the glasses' WebView keeps it), and start sessions
   without a server: phone and glasses agree at pairing time on fixed ICE credentials, persistent
   DTLS certificates and the phone's address (like the PC's peer-reflexive trick). Big and fragile.

## Security

The direct mode opens **no network port and no socket**. The only way in is Meta's DAT link to
the glasses registered with this app in the Meta AI app, plus the MediaProjection consent tapped on
the phone for every session. Keep the phone mode rules that still apply: typed text never presses
Enter, the glasses never name an app or a URL (previous/next only), nothing typed or seen is logged.

## Questions for the user

1. OK to install Android platform 36 and turn on Developer Mode in the Meta AI app?
2. What frame rate is good enough for the phone-direct mode (say 5 fps for reading and tapping)?
3. Keep the PC-relayed phone mode (for the web app's smooth video at home), or retire it if
   Direct works?
4. Is the phone unlocked in a pocket on the go? MediaProjection stops when it locks (Android 15+).

## Sources (read 2026-09-29)

- https://wearables.developer.meta.com/docs/develop/dat/display-android/
- https://wearables.developer.meta.com/docs/reference/android/dat/1.0/com_meta_wearable_dat_inputs_types_inputevent
- https://github.com/facebook/meta-wearables-dat-android (CHANGELOG: `image(bitmap)`, `mwdat-inputs`, `mwdat-speech`)
- https://github.com/facebook/meta-wearables-dat-android/discussions/95 (0.7: Display)
- https://wearables.developer.meta.com/llms.txt?full=true (BLE baseline, Wi-Fi for camera, one session per device)
- https://wearables.developer.meta.com/docs/develop/webapps (HTTPS public URL only; nothing on offline)
