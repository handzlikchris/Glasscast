# Native glasses app through Meta's DAT: looked at, not pursued

Status: **decided against, 2026-09-29.** Kept so nobody re-derives it. The plan page has the same
as "Track 4 · Native app" (https://claude.ai/artifact/UUBNEYcPv88tssxSezHPVV).

## The question

Would a native phone app that draws on the glasses through Meta's **Wearables Device Access
Toolkit (DAT) 1.0** (Display, Inputs, Speech modules) work better than the glasses web app plus
the Android companion, mainly for driving phone apps such as Claude, and with no internet?

## Answer: no, for driving the phone

- **DAT Display has no live video.** `sendContent` replaces the whole screen, with a local
  `Bitmap` image at best: tens of KB per frame, no frame-to-frame compression. Its video player
  only plays MP4 from an **https URL**, ≤ 400 px per side, ≤ 70,000 px, one per session.
- **The SDK picks the link;** its docs warn about Bluetooth bandwidth for images (Wi-Fi is only
  mentioned for camera streaming). Phone mode's WebRTC sends hardware H.264 over the glasses'
  Wi-Fi link to the phone, and the user finds it fine.
- The cursor would be drawn on the phone into each frame, so it moves at the frame rate.
- Input is about equal: `InputEvent` has `Nav`, `Select`, `Back`, `Button`, `Capture`, `Drag`
  (`x`, `y`, `dx`, `dy`).
- The only real gain would be starting a session with no internet, and the user accepts needing
  an initial connection.
- **The companion already is the native app** (capture, accessibility, input). Usability work
  (swipes, app switching) goes into `client-web/src/swipes.ts` and the companion either way.

Losing the connection mid-session ends it today, by our code rather than the media path: the
glasses end on their session socket closing (`PhoneScreen.tsx`), `PhoneRelay` ends and sends
`sessionEnd`, and `CompanionService` ends on `onDisconnected`. Reconnect resumes; the user is
fine with that for now. If it ever matters, the fix is to let a live phone session outlive the
signalling link (the DataChannel carries its own ping).

## Extras a web app can't reach (for later, in the companion, next to the web app)

| Extra | Needs DAT? | Notes |
| --- | --- | --- |
| Voice dictation, push-to-talk | No | The glasses are a Bluetooth headset: record their mic with `AudioRecord` + `setCommunicationDevice` (HFP/SCO). Transcribe on the phone or on the PC (Whisper). Recording drops the glasses to call-quality audio. The composer already covers basic voice typing |
| Cards when no session is open | Yes | E.g. "Claude has finished" with the reply's first lines |
| Physical buttons | Yes | Capture / action button as shortcuts |
| Glasses camera | Yes | See `companion-sensor-bridge.md` |

**Open before any DAT extra:** only one DAT session runs on the glasses at a time, and the docs
don't say whether our web app counts. Test with Meta's sample first (C0 in the sensor-bridge
brief). DAT needs Developer Mode in the Meta AI app, `minSdk 31`, `compileSdk 36` (platform 36
isn't installed on this PC; ask first).

## Sources (read 2026-09-29)

- https://wearables.developer.meta.com/docs/develop/dat/display-android/
- https://wearables.developer.meta.com/docs/reference/android/dat/1.0/com_meta_wearable_dat_inputs_types_inputevent
- https://github.com/facebook/meta-wearables-dat-android (CHANGELOG: `image(bitmap)`, `mwdat-inputs`, `mwdat-speech`)
- https://wearables.developer.meta.com/llms.txt?full=true
- https://wearables.developer.meta.com/docs/develop/webapps
