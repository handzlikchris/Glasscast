# Task: companion phone app — glasses camera and microphone into the web app

Status: **planned, not built**. Written 2026-09-25 at the end of the session that built M0–M4;
plan added the same day (plan page, "Companion app track"). Waiting on the open questions there.
Pick this up in a fresh session. Read `CLAUDE.md` first, then the plan page
(https://claude.ai/artifact/UUBNEYcPv88tssxSezHPVV, read it with the Artifact tool).

## Why

Web apps on Meta Ray-Ban Display get motion, orientation, phone GPS, Neural Band input
and local storage, but **no camera and no microphone** (`getUserMedia` fails). Phone apps
can reach the glasses' camera and audio through Meta's **Wearables Device Access Toolkit
(DAT)**. The user wants to keep the fast web-app workflow (update by URL, no store, no
rebuilds) and add a **thin, rarely rebuilt companion phone app** that only relays sensor
data. All the logic stays in the server and web app.

## Target shape

```
glasses camera/mic ──Bluetooth (DAT)──► phone companion app ──HTTPS/WSS──► server (this PC) ──► glasses web app
```

- **Companion app (phone):** a dumb pipe. It pairs once, keeps an authenticated connection to
  the server, takes commands (`still`, `stream start/stop`, `audio start/stop`) and uploads the
  results. No product logic, so it rarely needs rebuilding.
- **Server:** relays, and can process. For example it can transcribe speech locally (offline
  Whisper-class models; the user has Handy installed on this PC), type the text into Windows
  via the existing `typeText` path, and forward stills or transcripts to the glasses.
- **Glasses web app:** unchanged transport. It already holds a continuous session WebSocket
  (plus WebRTC video) to the server, so the server can push stills, transcripts and status.
- Why not phone → web app directly: the glasses browser only loads HTTPS, and a trusted
  certificate for a local phone server is painful. Going through the server avoids both.
- No browser camera permission is involved. The feed arrives as data over our own socket, so the
  server's `Permissions-Policy: camera=(), microphone=()` stays as it is.

## First milestone: "Snap"

Prove the whole path with the smallest useful feature:

1. A **Snap** button on the glasses (e.g. in View mode) sends `{type:"snap"}` to the server.
2. The server asks the connected companion app for a still.
3. The app captures one photo from the glasses camera via DAT and uploads it to the server.
4. The server pushes it to the glasses. It could show as an overlay or a new "Photo" view;
   a picture-in-picture over the desktop view is fine for a spike.

Acceptance: from the glasses (or the laptop/phone stand-in), tap Snap and see the photo on the
600×600 view, with the time from tap to photo measured and written down. Then, as a second step,
audio: stream the mic to the server, transcribe, and show or type the text.

## Checked on 2026-09-25 (docs, CHANGELOG and Meta's Android sample; nothing run yet)

The plan built from these is on the plan page, "Companion app track" (sections 11–19).

- **DAT 1.0.0** came out on 2026-09-24 on Maven Central (`com.meta.wearable:mwdat-core`, `-camera`,
  `-display`, `-inputs`, `-motion`, `-speech`, `-mockdevice`). No GitHub token needed any more.
- It supports **Meta Ray-Ban Display** on glasses firmware **V128** with Meta AI app **V290**. SDK 0.9.0
  needs Display V125 and app V282.
- Meta's `samples/CameraAccess`: `minSdk 31` (Android 12), `compileSdk 36`, JDK 17, Android Studio
  Narwhal or newer. Its foreground service is `connectedDevice`; the mic comes over HFP/SCO through
  `AudioRecord` + `setCommunicationDevice`. `stream.capturePhoto()` needs a running stream and
  returns `PhotoData.Bitmap` or `PhotoData.HEIC` (orientation in EXIF).
- **Developer Mode** in the Meta AI app (Settings → App info, tap the version five times) lets an
  unpublished app register. The app ID and client token can stay empty. Sideload with `adb`.
- One DAT session per device. It pauses or stops when "another app or system feature" starts a
  session. **Whether our glasses web app counts is unknown**: that's the first thing to test (C0).
- The "Android 10" user agent is Chrome's frozen value; the real Android version is still unknown.
- This PC has JDK 17, the Android SDK up to platform 35, `adb`, and Android Studio 2024.1 and 2024.3.
  Platform 36 isn't installed yet (ask first).

## Research notes (verify before relying on them)

These come from web sources read on 2026-09-25, not from hands-on use:

- DAT gives phone apps: photo capture, video streaming, microphone and audio, and (on Ray-Ban
  Display) the in-lens display. There are iOS and Android SDKs, with GitHub repos linked from Meta's
  announcement. It is in **developer preview**: install on your own devices and share with testers in
  your organization, but **no public publishing** yet. GA was targeted for 2026 and looks unlikely
  as of September.
- A community bridge (github.com/amanshah0729/vision) does this on iOS and reports:
  - video 504×896 HEVC at ~30 fps over Bluetooth
  - stills ~3.7 s warm and ~34 s cold (first in a session)
  - display updates 40–100 ms
  - iOS needs `UISupportedExternalAccessoryProtocols: [com.meta.ar.wearable]` and
    `UIBackgroundModes: [bluetooth-central, external-accessory]`, otherwise the connection drops
    after exactly 30 s
  - the app survives backgrounding (silent audio session) but not iOS terminating it
  - its bridge protocol is language-neutral: registration, capabilities, command queue, results
- The user's phone appears to be **Android** (the browser's user agent reported "Android 10";
  may be a generic value). Android's foreground services are usually friendlier for a long-running
  relay, but that isn't verified for DAT.

## Constraints (from CLAUDE.md, apply here too)

- **Security:** the companion app must pair through the **same human approval popup** on the PC,
  with a code comparison and a single-use token exchanged for a session. Its connection must be
  authenticated, Origin/rate-limited as appropriate, and never unauthenticated. Camera and audio
  data are sensitive: only to this server, never logged, never stored longer than needed.
  No new public ports: reuse the public host (`Web:PublicHost`, Caddy on 8443) and add
  paths to the Caddy allowlist deliberately.
- **Protocol:** extend `ControlProtocol.cs` and `client-web/src/protocol.ts` together, strictly
  (allowlisted types, size caps). Binary uploads (JPEG, audio) need their own size limits.
- **Don't break M0–M4:** keep `dotnet test`, the client tests and the e2e harness green. Extend the
  harness with a fake companion client, so Snap can be tested without the phone.
- **Working style:** small, logical commits with conventional prefixes and a body. Ask before
  installing SDKs or tools (Android Studio, Xcode, Java, etc.). Update the plan page **in place**,
  adding a "Companion app" track. Don't resolve the user's comment threads.

## Open questions for the user (ask early)

1. Which phone, iOS or Android? Is the Meta developer account and DAT preview access set up?
2. Which glasses model does DAT currently list as supported? Check that Ray-Ban Display is included.
3. What's the audio for: dictation to Windows, commands, or feeding Claude later? This sets
   whether to transcribe on the server, on the phone, or both.
4. Should the companion app pair per session, or stay paired longer? (The current model is
   single-use per session; keep that unless the user decides otherwise.)

## Suggested first steps

1. Read the DAT docs and the SDK repos for the user's platform. Confirm the capabilities, the
   supported glasses, the permissions, and the background limits. Update the notes above with
   what you actually find.
2. Sketch the pairing and protocol for the companion connection in the plan page. Get the user's
   OK before coding.
3. Server side first, with a fake companion client in tests and the harness: `snap` request →
   companion command → upload → push to glasses. Then the glasses UI. Then the real phone app.

## Sources

- https://developers.meta.com/blog/introducing-meta-wearables-device-access-toolkit/
- https://developers.meta.com/blog/build-for-display-glasses/
- https://wearables.developer.meta.com/docs/develop/webapps
- https://github.com/amanshah0729/vision
- https://vr.org/articles/meta-glasses-three-tiers-wearables-toolkit-publishing-connect-2026
