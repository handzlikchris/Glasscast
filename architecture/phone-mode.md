# Phone mode: controlling the Android phone from the glasses

Status (2026-09-29, branch `feat/phone-mode`): P1–P3 built and used from the glasses (video and
input, `live (local)`); P4 started (Region, Fit). **Redesigned 2026-09-29:** the glasses pair
with the **phone**, not the PC (works on the device), and a running session no longer needs the
PC. It does need the phone online: see "What it can't survive".

The first screen of the glasses app asks **PC or Phone**. PC is everything else in this repo.
Phone connects to an **Android companion app** on the user's Samsung S25 (unrooted, no ADB at
run time), which streams its screen to the glasses and injects the glasses' taps, swipes, Back /
Home / Recents and typed text.

## Shape

```
         first connection only: pairing, proof, offer/answer, ICE (opaque to the server)
glasses web app ──WSS /ws/session {type:"phone"}──► server ◄──WSS /ws/companion── companion app (phone)
      ▲   │                                         PhoneRelay                            │   ▲
      │   └──────────── WebRTC DataChannel "input": tap, swipe, nav, text, ping, end ─────►│   │
      └──────────────── WebRTC video: the phone's screen (MediaProjection, HW H.264) ─────┘   │
                        (through Meta's app on the phone, over its Wi-Fi Direct link) AccessibilityService
```

1. The glasses open a **relay** through the server (today this PC; anything that serves the web
   app and holds the companion's socket would do).
2. **Pairing** (first time) and **proof** (every session) run between the glasses and the phone
   through it. The phone decides; the server holds no secret.
3. The phone asks for Android's capture consent, then offers WebRTC (video + DataChannel), its
   offer MACed with the session key; the glasses check it and answer, MACed too.
4. When the DataChannel opens, **the glasses close the relay**. From then on the session lives
   on the direct connection only: it survives losing the server or this PC, but not the phone
   losing internet (below).

## Why it's built this way

### Why the first connection needs a server both can reach

The glasses' web app can't talk to the phone until a WebRTC connection exists, and setting one up
needs a few messages exchanged first (offer, answer, candidates). On a stock Android phone there
is no way for the page to send those straight to the phone:

- **The page is HTTPS, from a public URL** (Meta requires it), so it may only open secure
  connections: `wss://` or `https://`. A plain `ws://` socket to the phone is blocked as mixed
  content.
- **The phone has no name and no certificate.** A trusted certificate is issued for a domain
  name, not for a private address like 192.168.x.x. Giving the phone one means a public DNS name
  pointing at its private address (which changes with each network), a Let's Encrypt DNS
  challenge (GoDaddy's API is restricted now), the key on the phone, and renewals. And offline,
  the name can't even be looked up.
- **The phone would have to listen on a port**, reachable by anyone on the same Wi-Fi (a café).
- Browsers are also tightening what a public page may reach on private addresses (Local Network
  Access). What the glasses' WebView allows is unknown and undocumented.
- The one browser feature made for this, **WebTransport with a certificate fingerprint**, needs
  an HTTP/3 server on the phone (few Android libraries), certificates that expire every 14 days,
  and support in the glasses' WebView, which is unknown.
- Meta's web-app docs offer no channel between a glasses web app and a phone app.

So the first meeting happens on a server both can reach: the glasses load the page from it
anyway, and the companion keeps a socket open to it. That server needs to be online **only to
start a session** (the user's choice: "an initial connection is fine"). It relays and decides
nothing: the phone pairs and checks the glasses, and a tampering server would only make the
pairing codes differ (see below).

### Why WebRTC, not a native glasses app (Meta's Device Access Toolkit)

Considered and not pursued on 2026-09-29 (`.claude/tasks/phone-direct-dat.md`). DAT's Display
module lets a phone app draw on the glasses, but only as **whole still pictures**: each
`sendContent` replaces the screen with a bitmap (tens of KB each, no frame-to-frame
compression), over a link the SDK picks (its docs warn about Bluetooth bandwidth). Its video
player only plays MP4 files from an https URL, at most 400 px. WebRTC sends **compressed video**
(the phone's hardware H.264 sends only what changed, so a still screen costs almost nothing)
over the glasses' Wi-Fi link to the phone, with the cursor drawn locally on the glasses. The
companion is already native Android (capture, accessibility, input), so a native glasses app
would add nothing there.

### Why the session outlives the server

Video and input never went through the server; only the setup did. Before 2026-09-29 the
session still ended when the glasses' or the companion's socket to the PC dropped. Now each side
watches the other over the DataChannel instead: the glasses ping every 2 s and end after 10 s of
silence; the phone ends after 15 s.

### What it can't survive: the phone losing internet (measured 2026-09-29)

The hope was a session that survives dead spots. It can't, and nothing on our side can change
that:

- The glasses' WebView sits on a virtual network (its candidates: `10.0.2.2` and an
  `fdff:…:cafe` address). **Meta's app on the phone forwards its traffic** over a Wi-Fi Direct
  group it runs (`p2p-wlan0-0`, the phone at `192.168.49.1`), and the packets reach our socket
  from Meta's app, at one of the phone's own addresses. That group is the glasses' only IP path.
- **Meta's app deletes that group when the phone loses internet**, and doesn't bring it back
  while offline (phone logs: mobile data off at 14:31:14, group deleted the same second, none
  since). The glasses then show "no internet" and the web app stops. With the Wi-Fi radio off
  the group goes too (it runs on the same radio).
- It survives losing one network while another carries internet (SIM off on home Wi-Fi: the
  session carried on). Moving between networks (leaving home Wi-Fi for mobile data) makes Meta
  rebuild the group, and the session drops within seconds; Reconnect starts a new one.
- So: phone mode needs the phone online, like the web app itself. Truly offline would take
  Meta's native Device Access Toolkit over Bluetooth, set aside for its still-frame video
  (`.claude/tasks/phone-direct-dat.md`).

### iPhone and iPad: not feasible as it stands (looked at 2026-09-29)

- **Showing the screen: possible.** ReplayKit's broadcast upload extension captures the whole
  screen (as Zoom and Teams share it); the user starts it from Control Center or a picker each
  session, and the extension's memory limit (about 50 MB) still fits H.264 over WebRTC.
- **Controlling it: not possible for an app.** iOS has no public way for an app to tap, swipe or
  type into other apps (no equivalent of Android's AccessibilityService); remote-control apps
  are view-only on iPhone and iPad for that reason.
- **The only route to control is hardware:** iPadOS takes a Bluetooth mouse and keyboard (and
  iPhone does through AssistiveTouch), so a device posing as one (an ESP32, or an Android phone
  using its Bluetooth HID device role) could move a pointer, tap and type. A separate project,
  with a pointer to move rather than taps at a point.
- So: a view-only iPad companion would be modest work; control needs the hardware route. Not
  planned.

## Pairing and authentication

Between the glasses (`client-web/src/phoneTrust.ts`) and the phone (`GlassesTrust.kt`,
`GlassesRelay.kt`); the server passes the messages on unread. base64url without padding;
one test vector checked on both sides.

**Pairing** (numeric comparison with a commitment, as Bluetooth does):

1. glasses → phone `pairStart{commit}`: SHA-256 of a fresh P-256 public key.
2. phone → glasses `pairKey{key}`: its fresh public key.
3. glasses → phone `pairReveal{key}`: the phone checks it against the commitment.
4. Both: Z = ECDH, `prk = HMAC("glasses-remote/pair/v1", Z‖gPub‖pPub)`; `code` = six digits of
   `HMAC(prk,"code")`, `key = HMAC(prk,"key")`, `id = HMAC(prk,"id")[0..12]`.
5. The glasses show the code; the phone shows it in a notification with **Approve / Reject**
   (no answer in 60 s = Reject). Approve → the phone keeps `{id, key}` and says `paired`; the
   glasses keep it (localStorage) and go on to the proof on the same relay.

A server in the middle would have two different ECDH secrets, so the codes on the two screens
would differ. The commitment makes the glasses choose their key before seeing the phone's, so a
middle can't search for two keys that happen to give the same six digits.

**Every session**:

1. glasses → phone `hello{id, nonce}` (16 random bytes).
2. phone → glasses `challenge{nonce, mac}`: `sk = HMAC(key, "session"‖gNonce‖pNonce)`,
   `mac = HMAC(sk, "phone")`. The glasses check it: otherwise it isn't their phone.
3. glasses → phone `proof{mac = HMAC(sk, "glasses")}`. Only now does the phone ask for the
   capture consent.
4. The phone's `rtcOffer` carries `HMAC(sk, "offer\n"‖sdp)` and the glasses' `rtcAnswer`
   `HMAC(sk, "answer\n"‖sdp)`: the DTLS fingerprints in the SDP are tied to the paired devices,
   so a middle can't put itself into the WebRTC connection.

Unknown id → `authFailed`: the glasses forget their pairing and pair again on the same relay.
One pair of glasses at a time on the phone (a new pairing replaces it); **Forget the glasses** on
the companion's setup screen, **Pair again** on the glasses' ended screen.

## Decisions

| Topic | Choice | Why |
| --- | --- | --- |
| Transport | WebRTC: phone offers video + DataChannel; the server relays the setup only | Compressed video, lowest latency; input doesn't go through a server |
| First connection | Through a server both reach (the one serving the web app) | The page can't reach the phone before WebRTC (above) |
| Who decides | The phone: pairing approved on the phone, proof on every session, capture consent | The glasses control the phone, so the phone is the gate; the server stays dumb |
| Session life | Independent of the relay once the DataChannel is open; pings both ways | The PC or its internet going away mustn't end it (user's request, 2026-09-29). The phone losing internet still does (Meta, above) |
| Companion ⇄ server | Registers once through the PC's Approve popup (companion token, hash on the PC) | So a random device can't sit on the relay pretending to be the phone |
| Capture | `MediaProjection`, **entire screen** (`createConfigForDefaultDisplay`), cropped to a region on the phone | Input mapping needs screen coordinates; single-app capture gives no window position |
| Frame size | Crop to the region, scale so the long side is ≤ 600; the glasses letterbox | No padding on the phone; `cropAndScale` on the GPU texture is cheap |
| Codec | H.264 preferred (hardware on the S25, proven decode on the glasses), VP8 fallback | Same as the PC path |
| Type | The PC session's `TypePanel`, same steps: text box and composer → **Send text** → **Send** (keys: Send, ⏎, ⌫). Send presses the app's own send button: a clickable view labelled "Send" or "Send …", the one nearest the text field (Claude, ChatGPT and WhatsApp take Enter as a new line); else the field's editor action (send/go/done); else Enter. ⏎ is a plain Enter. For 0.8 s after the panel moves focus to Send text or Send, that button ignores presses (`guardMs`) | Same habit on both targets; the composer's Insert pinch reached the page late on the S25 |
| Glasses controls | Pinch-drag moves the cursor, a pinch taps. Swipes from `swipes.ts`, shared with PC sessions. Bar: Back · Home · Apps · Notif · Type · Region · Fit · ? · End (? shows the shortcuts; End returns to the PC/Phone choice) | Same habits on both targets |
| Typing | As a keyboard first (the accessibility service is also an input method), else `ACTION_SET_TEXT` | Apps that draw their own text take keyboard input once their keyboard is open |
| Input | `AccessibilityService`: `dispatchGesture`, `performGlobalAction`, text | Public API, no ADB, no root |
| Coordinates | Glasses send 0..1 **within the video frame**; the phone maps through its current crop | The phone alone knows the crop |
| Cursor | Drawn on the glasses, never on the phone | Nothing to inject until a tap |

## Protocol

### Glasses ⇄ server (`/ws/session`, first message `{type:"phone"}`)

No login on the server; relays are rate-limited per IP (10 a minute) and overall (30), and last
at most 3 minutes (`Companion:RelayTimeout`). Server → glasses: `phoneStatus{state}` (`offline`:
no companion connected, waiting up to `Companion:StartTimeout`; `ready`: the phone is reached;
`asking`: consent dialog; `live`: capturing), `pong`, and everything the phone sends. Close
reasons: `phone offline`, `phone declined`, `phone ended`, `replaced` (newer glasses), `timeout`,
`rate limit`, `invalid message`.

Glasses → phone (`RelayProtocol.cs`): `pairStart{commit}`, `pairReveal{key}`, `hello{id,nonce}`,
`proof{mac}`, `rtcAnswer{sdp,mac}`, `iceCandidate{candidate,sdpMid,sdpMLineIndex}`; `ping{t}` is
answered by the server. Keys, nonces and MACs are base64url of exact sizes. Anything else
(input above all) closes the relay as a violation.

Phone → glasses (`CompanionProtocol.cs`): `pairKey{key}`, `paired`, `pairFailed`,
`challenge{nonce,mac}`, `authFailed`, `sessionState{asking|live}` (→ `phoneStatus`),
`sessionState{declined|ended}` (close the relay), `rtcOffer{sdp,mac}`, `iceCandidate`.

### Companion ⇄ server (`/ws/companion`)

- First message, within 3 s: `pair{name}` (≤ 32 chars) or `auth{token}`.
- Registration: server → `pairCode{code, expiresInSeconds}`, then `paired{token}` or
  `pairFailed`, close.
- Authenticated: server → `authenticated`, `relayOpen` (glasses arrived), `relayClosed` (they
  left: drop whatever wasn't connected yet), the glasses' relayed messages, `pong{t}`.
  Phone → server: the phone's relayed messages above, `ping{t}`.
- Strict allowlist, 16 KB cap, rate-limited; anything else closes the socket. One companion
  connection at a time (a new one replaces the old), one relay at a time (newest wins).

### Glasses ⇄ phone (DataChannel `input`, JSON)

- Glasses → phone: `tap{x,y}`, `doubleTap{x,y}`, `touch{phase,x,y}` (`down`/`move`/`up`: a finger
  held down by tap-and-a-half, ~25 moves a second; lifted when the session ends),
  `longPress{x,y}` (still parsed, no longer sent), `swipe{x1,y1,x2,y2,ms}` (ms 50..2000),
  `nav{action}` (`back`, `home`, `recents`, `notifications`), `typeText{text}` (≤ 500 chars,
  flattened, never Enter), `key{key}` (`Enter`, `Backspace`, `Send`: the app's send button), `setRegion{x,y,width,height}`,
  `fitWindow{}`, `switchApp{dir}` (`previous`/`next`; still parsed, no longer sent: left twice
  now opens the app overview with `nav{recents}`, and the phone shows the whole screen until an
  app comes to the front, which Fit then follows), `ping{t}` (every 2 s: the phone's only
  sign the glasses are there), `end{}` (End on the glasses).
- Phone → glasses: `screen{width, height, region, follow, app?}`, `result{of, ok}`, `pong{t}`,
  `bye{reason}` just before it closes: `stopped` (Stop / End session on the phone), `capture`
  (the phone locked or its capture chip was tapped), `replaced` (newer glasses), `silent`.
- The companion parses these as strictly as `ControlProtocol` does.

## Security (additions to the invariants in CLAUDE.md)

- **The phone is the gate.** Glasses get in only by pairing (the code on both screens, Approve on
  the phone) and proving the pairing key on every session; then Android's capture consent is
  tapped on the phone for every session. The PC's Approve popup and device token play no part.
- **The server holds no secret and decides nothing.** It passes base64url keys, nonces and MACs;
  a server that tampered would make the codes differ (pairing) or the MACs fail (sessions, offer,
  answer). Relays need no login, so they're rate-limited; pairing prompts on the phone come at
  most every 10 s and once per relay.
- The server never carries the phone's video or the glasses' input; any input on a relay is a
  violation.
- The companion registers with the server once through the PC's Approve popup (companion token,
  hash on the PC, **Forget phone** in the tray); `/ws/companion` refuses any request with an
  `Origin` header (web pages).
- Keys: the glasses keep `{id, key}` in localStorage (like the PC's device token: never in React
  state, a URL or a log); the phone keeps it in app-private storage (no backups). Never logged.
- The companion accepts input only on the DataChannel of the peer whose answer carried the
  right MAC. Strict parser, rate limit. Typed text never presses Enter.
- The phone shows a notification while live, with **End session**; Android's status-bar chip
  can stop the capture too. The phone locking stops it.
- The glasses can't name an app or a URL (previous/next only, from the phone's own recent list).
- Web Crypto needs a secure context: phone mode works over HTTPS (the public path), not the dev
  `lan` profile's plain http.

## Milestones

| # | What | Needs the phone? |
| --- | --- | --- |
| P0 | Feasibility on the S25: install, allow restricted settings, accessibility, capture consent, WebRTC to the glasses at home **and** on 5G | yes (home Wi-Fi done: `live (local)`) |
| P1 | Server: companion registration, `/ws/companion`, `PhoneRelay` | done |
| P2 | Glasses: PC/Phone chooser, phone session screen | done |
| P3 | Companion app: pairing with the PC, foreground service, consent, capture → WebRTC, DataChannel → accessibility | done, used from the glasses |
| P3b | **Pairing on the phone, sessions that outlive the server** (2026-09-29) | done; pairing works on the device. Offline sessions: not possible (Meta) |
| P4 | Region: fit to the top app window, Samsung pop-up view, the PC-style Region mode. **Region and Fit built**; rotation and opening apps in pop-up view still to do | yes |
| P5 | On the go: keep the screen on while live, lock handling | yes |
| P6 | Later: the phone's sound (`AudioPlaybackCapture`), stats figures, a fake phone in the e2e harness | yes |

## Research notes (2026-09-27; verify on the S25)

- **Capture stops when the phone locks.** From Android 15 QPR1, a running MediaProjection ends
  when the keyguard shows (with a PIN/fingerprint set). The companion keeps the screen on while
  live. Whether Samsung's "accidental touch protection" in a pocket blocks injected gestures is
  unknown.
- **Consent every session.** Since Android 14, the `createScreenCaptureIntent` result works once.
  The foreground service must be of type `mediaProjection` and start **after** consent.
- **Starting the consent dialog from the background:** apps with a bound AccessibilityService are
  exempt from Android's background-activity-start limits. The notification is the fallback.
- **Accessibility for a sideloaded app:** App info → ⋮ → **Allow restricted settings**, then enable.
- **Samsung windows:** pop-up view windows resize by hand; accessibility's `getWindows()` gives
  each window's bounds, which is how Fit crops to the app window.
- **Protected content** (banking apps, DRM video, `FLAG_SECURE`) captures black.
- **WebRTC library:** `io.getstream:stream-webrtc-android` 1.3.10.
- **Network on the go:** the glasses' WebView reports no network of its own (`netType` 8); its
  traffic goes through Meta's app on the phone (measured, see "What it can't survive").

## Files

| File | Role |
| --- | --- |
| `server/Phone/PhoneRelay.cs` | A relay: waits for the companion, passes messages both ways, closes on decline, phone loss, replacement or timeout; tells the phone `relayClosed` |
| `server/Phone/RelayProtocol.cs` | Strict parser for the glasses' relay messages (base64url sizes, no input) |
| `server/Phone/CompanionEndpoint.cs` | `/ws/companion`: no-Origin check, 3 s first message, register or auth, receive loop (ping, rate limit, 45 s heartbeat) |
| `server/Phone/CompanionRegistry.cs` | Companion registration, its token hash (`companion-grant.json`), the live connection, relay rate limits |
| `server/Phone/CompanionLink.cs` | One authenticated companion connection; its one relay (`RelayHandle`) |
| `server/Phone/CompanionProtocol.cs` | Strict parser for the companion's messages; `PhoneState` |
| `server/Ui/ApprovePopup.cs`, `TrayApp.cs` | The popup registers a phone; tray **Forget phone** |
| `client-web/src/App.tsx`, `target.ts` | The PC/Phone first screen; the last choice in localStorage |
| `client-web/src/PhoneScreen.tsx` | The phone session: video, local cursor, gestures, bar, Type, the pairing code panel, pings |
| `client-web/src/phoneConnect.ts` | Relay → pairing or proof → checked offer, signed answer; closes the relay once connected |
| `client-web/src/phoneTrust.ts` | The crypto and the stored pairing |
| `client-web/src/phoneSignal.ts` | The relay socket and its parser |
| `client-web/src/phoneRtc.ts` | Answerer with trickled candidates and the phone's DataChannel |
| `client-web/src/phoneProtocol.ts` | DataChannel messages, parser, frame mapping, swipes |
| `android-companion/…/GlassesRelay.kt` | The phone's side of a relay: pairing prompt, proof, hands over the session key |
| `android-companion/…/GlassesTrust.kt` | The crypto (same vector as `phoneTrust.test.ts`) |
| `android-companion/` | The rest of the companion app (see its README) |

Tests: `tests/Phone/PhoneEndpointTests.cs` (a fake companion over real sockets: relay both ways,
no PC state touched, violations, decline, phone loss, replacement, rate limit),
`tests/Phone/RelayProtocolTests.cs`, `tests/Phone/CompanionProtocolTests.cs`,
`client-web/src/phoneTrust.test.ts` (the vector), `phoneConnect.test.ts` (a fake phone: pairing,
proof, re-pairing, a phone that can't prove itself, relay loss before and after connecting),
`phoneSignal.test.ts`, `phoneProtocol.test.ts`, `android-companion/…/GlassesTrustTest.kt` (the
vector), `GlassesRelayTest.kt` (fake glasses), `InputProtocolTest.kt`. The e2e drive presses PC
on the first screen; it has no phone path yet.

## Next steps

1. A fake phone for the e2e harness (SIPSorcery offering a test pattern and a DataChannel, doing
   the phone's half of pairing), so the phone path is tested end to end without the phone.
2. P4 on the device: rotation, opening apps in pop-up view.
