# Phone mode: controlling the Android phone from the glasses

Status (2026-09-27, branch `feat/phone-mode`): **P1 and P2 built and tested; P3 builds and is
installed on the S25** (Android 16, One UI 8.0). First sessions from the glasses work, over the
LAN too. **P4 started:** Region (a square chosen on the glasses) and Fit (follow a pop-up window). The research notes below
come from docs and web sources, not from the device.

The first screen of the glasses app asks **PC or Phone**. PC is everything that exists today.
Phone connects to an **Android companion app** on the user's Samsung S25 (unrooted, no ADB at
run time), which streams its screen to the glasses and injects the glasses' taps, swipes, Back /
Home / Recents and typed text.

## Shape

```
                    signalling only (SDP, ICE, start/stop)
glasses web app ──WSS /ws/session {target:"phone"}──► PC server ◄──WSS /ws/companion── companion app (phone)
      ▲   │                                           PhoneRelay                          │   ▲
      │   └──────────── WebRTC DataChannel "input": tap, swipe, nav, text ───────────────►│   │
      └──────────────── WebRTC video: the phone's screen (MediaProjection, HW H.264) ─────┘   │
                                                                   AccessibilityService ──────┘
```

- **The PC is only the meeting point.** It already serves the web app (the glasses can't load it
  without the PC being reachable), has the certificate, the Origin rules and the glasses'
  pairing. It relays the handful of signalling messages and nothing else: no video, no input.
- **Video and input go straight between phone and glasses** over one WebRTC peer connection the
  phone offers (video track + a DataChannel). On the go the glasses' traffic is relayed by the
  phone anyway, so the path should be phone → phone. Whether ICE finds it is the first thing to
  test (P0).
- **libwebrtc on the phone** (`io.getstream:stream-webrtc-android`) does capture
  (`ScreenCapturerAndroid`), hardware H.264, pacing, NACK/PLI and congestion control, so none
  of the PC's hand-built media pipeline is needed there.

### Why not phone ↔ glasses without the PC

- The glasses load only HTTPS pages, from a public URL. The phone has no stable name or
  certificate, and a page can't open `ws://` to a LAN or phone address (mixed content). Meta's
  web-app docs say nothing about reaching a phone app from the page.
- Signalling needs a rendezvous both sides can reach from anywhere. The PC is it, today. A later
  option: host the static client elsewhere and a tiny relay in the cloud (not planned).

## Decisions

| Topic | Choice | Why |
| --- | --- | --- |
| Transport | WebRTC: phone offers video + DataChannel; PC relays signalling only | Lowest latency; input doesn't make a trip through the PC |
| Capture | `MediaProjection`, **entire screen** (`createConfigForDefaultDisplay`), cropped to a region on the phone | Input mapping needs screen coordinates; single-app capture gives no window position |
| Frame size | Crop to the region, scale so the long side is ≤ 600; the glasses letterbox | No padding on the phone; `cropAndScale` on the GPU texture is cheap |
| Codec | H.264 preferred (hardware on the S25, proven decode on the glasses), VP8 fallback | Same as the PC path |
| Glasses controls | Pinch-drag moves the cursor, a pinch taps (anything shorter than a 0.6 s long press). Swipes from `swipes.ts`, shared with PC sessions: up/down scroll, left/right page (0.3 s late), right twice opens Type, left twice presses the phone's Back. Bar: Back · Home · Apps · Notif · Type · Region · Fit · End (End returns to the PC/Phone choice) | Same habits on both targets (user's request, 2026-09-27) |
| Input | `AccessibilityService`: `dispatchGesture` (tap, long press, swipe), `performGlobalAction` (Back/Home/Recents/notifications), `ACTION_SET_TEXT` + `ACTION_IME_ENTER` | Public API, no ADB, no root |
| Coordinates | Glasses send 0..1 **within the video frame**; the phone maps through its current crop | Same idea as the PC's `move`; the phone alone knows the crop |
| Cursor | Drawn on the glasses (overlay), never on the phone | Nothing to inject until a tap |
| Glasses auth | The existing approval / device token, with `target: "phone"` in `authenticate`/`resume` | No new glasses credential; one session at a time still holds |
| Companion auth | Pairs once through the PC's **Approve popup** (code on phone and popup), then a 256-bit companion token (hash on the PC, `companion-grant.json`), forgettable in the tray | Same pattern as the glasses; a fake "phone" must never receive the glasses' input |
| Per-session gate on the phone | Android's own MediaProjection consent dialog, every session (Android 14+ makes the token single-use) | A human on the phone agrees to each session anyway |
| Companion socket | `/ws/companion`; a request **with** an `Origin` header is refused (browsers always send one) | Native client; no web page can open it |
| Session driver | The glasses' session socket (`PhoneRelay`): it asks the companion to start, relays, and ends both sides together | One lease, one lifetime, same watchdogs |

## Protocol

### Glasses ⇄ PC (`/ws/session`, target phone)

- First message: `authenticate{token, target:"phone"}` or `resume{token, target:"phone"}`.
  `target` is optional; absent or `"pc"` is today's behaviour.
- PC → glasses: `authenticated{…}`, then `phoneStatus{state}` with `state` one of
  `offline` (no companion connected), `asking` (consent dialog on the phone), `declined`,
  `live`; then `rtcOffer{sdp}` and trickled `iceCandidate{candidate, sdpMid, sdpMLineIndex}`
  (a new direction for that message).
- Glasses → PC: `rtcAnswer`, `iceCandidate`, `ping`. Nothing else is accepted in a phone session
  (input never goes through the PC).

### Companion ⇄ PC (`/ws/companion`)

- First message, within 3 s: `pair{name}` (≤ 32 chars) or `auth{token}`.
- Pairing: PC → `pairCode{code, expiresInSeconds}`, then `paired{token}` or `pairFailed`, close.
- Authenticated: PC → `authenticated`. Then:
  - PC → phone: `sessionStart{}` (glasses want the phone), `rtcAnswer{sdp}`,
    `iceCandidate{…}`, `sessionEnd{reason}`, `pong{t}`.
  - Phone → PC: `sessionState{state}` (`asking`, `declined`, `live`, `ended`), `rtcOffer{sdp}`,
    `iceCandidate{…}`, `ping{t}`.
- Strict allowlist, 16 KB cap, rate-limited; anything else closes the socket. One companion
  connection at a time (a new authenticated one replaces the old).

### Glasses ⇄ phone (DataChannel `input`, JSON)

- Glasses → phone: `tap{x,y}`, `doubleTap{x,y}`, `touch{phase,x,y}` (`down`/`move`/`up`: a finger
  held down by tap-and-a-half, moved in pieces as the glasses send it, ~25 a second; lifted when
  the session ends),
  `longPress{x,y}` (still parsed, no longer sent), `swipe{x1,y1,x2,y2,ms}` (ms 50..2000),
  `nav{action}` (`back`, `home`, `recents`, `notifications`), `typeText{text}` (≤ 500 chars,
  flattened, never Enter), `key{key}` (`Enter`, `Backspace`), `setRegion{x,y,width,height}`
  (0..1 of the phone screen; stops following a window, kept for the next session),
  `fitWindow{}` (crop to the top-most app window that doesn't fill the screen, e.g. a Samsung
  pop-up view window, and follow it), `ping{t}`.
- Phone → glasses: `screen{width, height, region{x,y,width,height}, follow}` (on connect and on
  every change), `result{of, ok}` for text and keys, `pong{t}`.
- The companion parses these as strictly as `ControlProtocol` does.

## Security (additions to the invariants in CLAUDE.md)

- The PC never relays video or input for the phone; it can't see either.
- The companion pairs only through the Approve popup; its token is hashed on the PC, stored in
  the app's private storage on the phone, never logged. Tray: **Forget phone**.
- Every phone session needs the MediaProjection consent tapped **on the phone**. The companion
  shows a notification while live, with **Stop**. Android's own status-bar chip shows capture.
- The companion accepts input only on the DataChannel of the peer it offered to, after DTLS
  (fingerprints exchanged over the authenticated signalling). Strict parser, rate limit.
- Typed text never presses Enter; Enter is a separate `key`.
- The companion never launches apps or opens URLs on the glasses' request (as on the PC).

## Milestones

| # | What | Needs the phone? |
| --- | --- | --- |
| P0 | Feasibility on the S25: install, allow restricted settings, enable accessibility, capture consent, WebRTC to the glasses at home **and** on 5G, a square pop-up window | yes |
| P1 | PC: companion pairing, `/ws/companion`, `PhoneRelay`, `target` in auth; tests with a fake companion | no |
| P2 | Glasses: PC/Phone chooser, phone session screen (video, cursor, tap, swipe to scroll, Back/Home/Recents, Type) | no (fake companion in the harness) |
| P3 | Companion app: pairing, foreground service, consent, capture → WebRTC, DataChannel → accessibility | build: no; run: yes |
| P4 | Region: fit to the top app window (accessibility window bounds), Samsung pop-up view helper, the PC-style Region mode. **Region and Fit built** (2026-09-27); rotation and opening apps in pop-up view still to do | yes |
| P5 | On the go: keep the screen on while live, lock handling, approval on the phone instead of the PC after the 24 h device token runs out, reconnect | yes |
| P6 | Later: the phone's sound (`AudioPlaybackCapture`), stats panel figures, app shortcuts configured on the phone | yes |

## Research notes (2026-09-27; verify on the S25)

- **Capture stops when the phone locks.** From Android 15 QPR1, a running MediaProjection ends
  when the keyguard shows (with a PIN/fingerprint set), and a status-bar chip shows while
  capturing. So the phone must stay unlocked with the screen on during a session. The companion
  can keep it on while live (a tiny accessibility overlay with `FLAG_KEEP_SCREEN_ON`). Whether
  Samsung's "accidental touch protection" in a pocket blocks injected gestures is unknown.
- **Consent every session.** Since Android 14, the `createScreenCaptureIntent` result works once,
  and one `MediaProjection` makes one virtual display. The foreground service must be of type
  `mediaProjection` and start **after** consent. `MediaProjection.Callback` must be registered
  before `createVirtualDisplay` (the libwebrtc capturer does this).
- **Starting the consent dialog from the background:** apps with a bound AccessibilityService are
  exempt from Android's background-activity-start limits, so the companion can raise the dialog
  when the glasses ask. To confirm on the S25.
- **Accessibility for a sideloaded app:** Android 13+ greys out the toggle for apps installed from
  a browser or file manager ("Restricted setting"); Android 15 blocks it even for `adb install`.
  Fix: App info → ⋮ → **Allow restricted settings**, then enable. One-off.
- **Samsung windows:** pop-up view (freeform) windows resize by hand; `ActivityOptions.setLaunchBounds`
  only applies in freeform mode. Split screen: `FLAG_ACTIVITY_LAUNCH_ADJACENT`, divider not
  controllable. Accessibility's `getWindows()` gives each window's screen bounds, which is how P4
  fits the crop to the app window.
- **Protected content** (banking apps, DRM video, `FLAG_SECURE`) captures black; some controls
  ignore accessibility input.
- **WebRTC library:** `io.getstream:stream-webrtc-android` 1.3.10 (Maven Central, June 2026) is
  the maintained prebuilt libwebrtc; it has `ScreenCapturerAndroid`, hardware encoders and
  `VideoProcessor` for cropping.
- **Network on the go:** the glasses' WebView reports no network of its own (`netType` 8) and its
  traffic leaves through the phone. Whether it can reach the phone's own addresses (Wi-Fi,
  cellular IPv4/IPv6) is unknown; P0 logs the chosen ICE pair. Fallbacks: a STUN server (server
  reflexive candidates), then a TURN relay on the PC.
- **Build tools on this PC:** Android SDK platforms up to 35, build-tools 35, JDK 17, AGP 9.0.0
  and Gradle 9.1.0 in the Gradle cache. The companion targets SDK 35, min SDK 30.

## Files

| File | Role |
| --- | --- |
| `server/Phone/CompanionEndpoint.cs` | `/ws/companion`: no-Origin check, 3 s first message, pair or auth, then the connection's receive loop (ping, rate limit, 45 s heartbeat) |
| `server/Phone/CompanionRegistry.cs` | Companion pairing (one request, rate limits, popup events), the token hash (`companion-grant.json`), the live connection, `WaitForLinkAsync` |
| `server/Phone/CompanionLink.cs` | One authenticated companion connection; the running session's inbox |
| `server/Phone/CompanionProtocol.cs` | Strict parser for the companion's messages; `PhoneState` |
| `server/Phone/PhoneRelay.cs` | A glasses session with `target:"phone"`: waits for the companion, relays, ends both sides |
| `server/Ui/ApprovePopup.cs`, `TrayApp.cs` | The same popup approves a phone; tray **Forget phone** |
| `client-web/src/App.tsx`, `target.ts` | The PC/Phone first screen; the last choice in localStorage |
| `client-web/src/PhoneScreen.tsx` | The phone session: video, local cursor, gestures, bar, Type |
| `client-web/src/phoneRtc.ts` | Answerer with trickled candidates and the phone's DataChannel |
| `client-web/src/phoneProtocol.ts` | DataChannel messages, parser, frame mapping, swipes (tested) |
| `android-companion/` | The companion app (see its README) |

Tests: `tests/Phone/CompanionProtocolTests.cs`, `tests/Phone/PhoneEndpointTests.cs` (fake
companion over real sockets: pairing, auth, relay both ways, violations, decline, loss, timeout,
forget, replace), `client-web/src/phoneProtocol.test.ts`, `protocol.test.ts` (relay messages),
`android-companion/.../InputProtocolTest.kt`. The e2e drive presses PC on the first screen; it
has no phone path yet.

## Next steps

1. Done: the companion builds (Gradle 9.1, build-tools 36) and is installed over wireless adb.
2. P0 on the S25 (the user): install, allow restricted settings, accessibility on, pair, Start;
   then Phone on the glasses, at home on Wi-Fi first, then on 5G. Read the glasses' status bar
   (`live (local|remote)`) and `adb logcat -s ScreenSession CompanionService`.
3. A fake phone for the e2e harness (SIPSorcery offering a test pattern and a DataChannel,
   recording input), so the phone path is tested end to end without the phone.
4. P4 on the device: does Fit find Samsung's pop-up window (the top-most app window that doesn't
   fill the screen) and follow it while it's dragged? Does One UI remember a pop-up's size per
   app? Then rotation, and opening an app in pop-up view from the glasses.

## Questions for the user

1. Which Android / One UI version is the S25 on (Settings → About phone → Software information)?
2. On the go, is the phone going to be unlocked in a pocket or bag? Capture stops on lock (above).
3. After the 24 h device token runs out away from home, approve on the phone instead of the PC (P5)?
