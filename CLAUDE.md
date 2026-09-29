# CLAUDE.md — Glasses Remote Desktop

Guide for AI agents (and humans) picking up this repo. Read this first, then `README.md`.
Before changing a feature, read its doc in **`architecture/`** (index: `architecture/README.md`):
pairing and auth, session and protocol, media pipeline, audio, stats, input and desktop, glasses
client, PC UI, deployment, testing. Each names its files, flows, rules and tests; keep the doc
current in the same piece of work.

## What this is

A proof of concept that lets a **Meta Ray-Ban Display** web app control this Windows PC:
view a region of the primary monitor over WebRTC video (with the PC's sound), move/click/scroll the mouse with
Neural Band gestures, switch between configured apps, and type through the glasses'
voice/handwriting composer. Every session needs a **single-use pairing approved in a popup on the PC**,
or (for 24 h after such an approval) the **device token** of the glasses that were approved.

**Phone mode (branch `feat/phone-mode`, 2026-09-27; redesigned 2026-09-29):** the first screen
asks **PC or Phone**. Phone controls the user's Android phone (Samsung S25) through a companion app
in `android-companion/` (MediaProjection + AccessibilityService). The server (this PC) is only the
meeting point for the **first connection**: the glasses pair with the **phone** (code on both,
Approve on the phone) and prove themselves to it each session; once the WebRTC DataChannel is open
the session doesn't need the server. It does need the **phone online**: Meta's app, the glasses'
only IP link to the phone, takes that link down when the phone loses internet (measured
2026-09-29), so a dead spot ends a session whatever we do. Why it's built this way (a page can't
reach a stock phone directly; WebRTC's compressed video beats DAT's still frames), design,
protocols, status: **`architecture/phone-mode.md`**.

- **Plan / design / decisions / setup / status** live in a shared page:
  https://claude.ai/artifact/UUBNEYcPv88tssxSezHPVV (read it with the Artifact tool, action `read`).
  Update it **in place** when decisions change. Never create a new plan artifact. (Its status
  section predates the on-device work of 2026-09-25; this file is more current.)
- Milestones: M0 network spike, M1 pairing, M2 streaming, M3 input, M4 hardening (all built),
  M5 validation on the glasses (**largely done 2026-09-25**: video, pairing, Pointer, Type and the
  composer work on the device; the controls were reworked there, see "Glasses controls").

## Stack

| Part | Tech |
| --- | --- |
| Server | .NET 10 (`net10.0-windows`, SDK pinned in `global.json`), ASP.NET Core + WinForms, one process as the logged-in user |
| WebRTC | SIPSorcery 10.0.16, send-only video, fixed UDP port, no STUN/TURN |
| Video | H.264 via Windows Media Foundation software encoder (Vortice.MediaFoundation 3.8.3); VP8 (libvpx via SIPSorceryMedia.Encoders) fallback |
| Audio | WASAPI process loopback (before the PC's volume; endpoint loopback fallback, NAudio.Wasapi 2.2.1) → Opus (Concentus, via SIPSorcery) on a second track, same port; Web Audio playback on the glasses |
| Capture / input | GDI `CopyFromScreen`; `SendInput` (P/Invoke); window switching via EnumWindows/SetWindowPos |
| Client | React 19, Vite 8 (Rolldown), TypeScript 7 (native `tsc`), Vitest 4 |
| Proxy | Caddy 2.11 (stock) terminates TLS, Let's Encrypt via TLS-ALPN |

## Repo map

```
server/                 GlassesRemote.Server (ASP.NET Core + WinForms)
  Program.cs            STA Main: DPI init, web host in background, tray on UI thread
  Hosting/              ServerApp.Create (DI + pipeline, shared with tests), GlassesEndpoints
                        (/health, /ws/pair, /ws/session, OriginPolicy), SecurityHeaders,
                        ClientCaching (no-cache page, immutable assets), Options
  Pairing/              PairingCoordinator (state machine), DeviceGrant (+ store), Secrets (tokens/codes),
                        SlidingWindowLimiter, PairingOptions
  Sessions/             ControlSession (one authenticated session), InputController (mode gating),
                        SocketIO (capped WS reads/writes), TokenBucket
  Protocol/             ControlMessages + ControlProtocol (strict allowlist parser)
  Desktop/              interfaces (IScreen, ICaptureSource, IInputInjector, IKeepAwake, IWindowSwitcher),
                        RegionMath, RegionStore, CastArea (region of the active session), AppShortcuts
  Media/                FramePump, SipsorceryMediaPeer, SdpCandidates, SdpFeedback, MfH264Encoder, Nv12,
                        Vp8 encoder, H264Rtp (packetizer) + RtpPacer (spreads packets out; uses
                        Windows/PreciseSleep), SentPackets + RtcpNack (resend what the glasses NACK), RtcpReadable,
                        BitrateController (target from the glasses' loss), LinkTest (diagnostic),
                        StatsLog (daily JSONL of glasses + PC media figures); audio: AudioPump,
                        AudioTimeline, OpusAudioEncoder, SdpStreams (separate msids)
  Windows/              Win32 implementations: SendInput, GDI capture, keep-awake, Win32WindowSwitcher,
                        ProcessLoopbackCapture + LoopbackAudioCapture (WASAPI), NativeMethods
  Ui/                   TrayApp, ApprovePopup, AlertsForm, SessionBanner, CastFrame (orange frame
                        around the cast area on the PC monitor), TerminateHotkey
  Alerts/               AlertLog, AlertThrottle
  Phone/                phone mode: CompanionEndpoint (/ws/companion), CompanionRegistry (companion
                        registration, token hash, live connection, relay limits), CompanionLink,
                        CompanionProtocol, PhoneRelay + RelayProtocol (glasses ⇄ phone, opaque)
client-web/             glasses client (600×600)
  build-label.mjs       stamps "Build <commit> · <time>" into the bundle (shown on the pairing screen)
  src/connection.ts     pair + session sockets; token lives ONLY here, in memory
  src/rtc.ts            receive-only RTCPeerConnection (video + audio elements) + stats
  src/audio.ts          ♪ setting (localStorage), stereo=1 answer fix-up, V/A bandwidth label
  src/audioOutput.ts    plays the PC's sound through Web Audio (a muted <audio> keeps it flowing)
  src/App.tsx           first screen (PC or Phone; last choice in target.ts), pairing, sessions, ended
  src/SessionScreen.tsx modes, gestures, focus handling, Back/history, overlay, edge panning
  src/PhoneScreen.tsx   phone session: phone's frame, local cursor, taps/swipes/nav/text over the
                        DataChannel (phoneRtc.ts, phoneProtocol.ts); phoneConnect.ts (relay, pairing,
                        proof), phoneTrust.ts (the crypto), phoneSignal.ts (relay socket)
  src/TypePanel.tsx     text box for the composer, Send text, shortcut keys, focus chain
  src/focusnav.ts       navigation model: Back targets, tap routing (pure, tested)
  src/swipes.ts         THE swipe rules for PC and phone: double left/right, what each does (pure, tested)
  src/shortcuts.ts      the ? panel's rows (swipe doubles taken from swipes.ts; ShortcutsPanel.tsx)
  src/pinchPress.ts     outside a session (pairing/ended screens), a pinch presses the focused button
  src/mediaStats.ts     Stats panel: capture-to-display latency (RTP timestamp matching, clock offset
                        from ping/pong), receiver counters, PC pump figures (pure, tested)
  src/{protocol,geometry,gestures,controls,display,scrollPrefs}.ts  pure logic with *.test.ts
  src/overlay.ts        canvas: cursor, region box, pan-edge glow
tests/                  xUnit: unit + WebSocket integration (TestServerHost) + real H.264 encoder
android-companion/      phone companion app (Kotlin, no AndroidX, libwebrtc + OkHttp); see its README
tools/e2e-harness/      DEV-ONLY host (auto-approves pairing, records input and app switches) +
                        browser/drive.mjs (headless Chrome, 48 checks)
deploy/                 Caddyfile, firewall.ps1
scripts/run.ps1         builds client if needed, runs server (-Dev, -Lan)
tools/bin/caddy.exe     local Caddy binary (git-ignored)
.claude/tasks/          task briefs for new sessions
architecture/           one doc per feature area (files, flows, rules, tests); start at README.md
```

## Commands

```powershell
dotnet build GlassesRemote.sln
dotnet test                                     # ~270 server tests, ~23 s
cd client-web; npm test; npx tsc --noEmit; npm run build   # ~110 client tests, typecheck, dist/
cd android-companion; .\gradlew.bat assembleDebug testDebugUnitTest   # companion app (see its README)
.\scripts\run.ps1 -Dev                          # local: http://127.0.0.1:5080
.\scripts\run.ps1 -Lan                          # other devices on the LAN (needs firewall.ps1 -LanTesting)
.\tools\bin\caddy.exe run --config deploy\Caddyfile          # public HTTPS
dotnet run --project tools/e2e-harness          # then: cd tools/e2e-harness/browser; npm run drive
```

Rider launch profiles (`server/Properties/launchSettings.json`): **server** (Production, real use
behind Caddy), **dev** (local), **lan** (Development on 0.0.0.0 with `AllowedHosts=*` and
`Web__AllowSameOrigin=true`).

**If the user's server is running (usually from Rider):** `server\bin\Debug\...\GlassesRemote.Server.exe`
is locked and normal builds fail. Build or test into a separate folder instead:
`dotnet test tests/GlassesRemote.Server.Tests.csproj -p:OutDir=E:/_src-unity/MetaDisplayRDP/tests/bin/isolated/`
and `dotnet build tools/e2e-harness -p:OutDir=E:/_src-unity/MetaDisplayRDP/tools/e2e-harness/bin/isolated/`.
Don't stop the user's server without asking. Run the harness on another media port while a
real session may be live: `Media__MediaPort=50002 ./tools/e2e-harness/bin/isolated/E2eHarness.exe`.
The isolated folders get a copy of the real `appsettings.Local.json`; delete it there before
running (its app shortcuts break `App_shortcuts_come_from_the_pc_config…`, its bind address the harness).
`dotnet test <csproj>` rebuilds and copies it back, so after deleting run the built dll instead:
`dotnet test tests/bin/isolated/GlassesRemote.Server.Tests.dll`.

**When the user asks Claude to run the server** (as on 2026-09-25): stop the old process, then
`dotnet build server` and `dotnet run --project server --launch-profile server --no-build` as a
background command, with output to a scratchpad log. Check `http://127.0.0.1:5080/health` and the
Caddy path (8443). Restart it yourself after server changes and say so. A Rider re-run that
can't rebuild (exe locked) silently leaves the old server running: check the process start time.

**Client changes go live without a server restart:** the running server serves `client-web/dist`
from disk, so `npm run build` is enough; the user presses Restart in the glasses' web app menu.
Build **after** committing so the label shows a clean commit (a `+` means uncommitted changes).
Server or `appsettings.Local.json` changes need the user to restart the server in Rider.

## Architecture in one screen

```
glasses/phone ──HTTPS+WSS──► router :443 ──► Caddy :8443 ──► 127.0.0.1:5080 server ──► SendInput
      ▲                                                          │ GDI capture → H.264 → SIPSorcery
      └──────────── WebRTC video: UDP 203.0.113.10:50000 ◄───────┘ (router → 192.168.1.114:50000)
```

1. `/ws/pair`: server opens a request (6-char code), the tray shows the **ApprovePopup**; Approve
   sends a 256-bit token down that pair socket only.
2. `/ws/session`: first message must be `{type:"authenticate",token}` or `{type:"resume",token}`
   within 3 s. The approval token is single-use, SHA-256 stored, consumed on use, dies with the
   session. `authenticated` then carries a **device token** (`deviceToken`, `deviceTokenExpiresAt`):
   the glasses resume with it later without the popup (see the security invariants).
3. Server sends `hello` (monitor, region, starting mode = **pointer**, codec, app shortcut names,
   `audio` = whether the offer carries the PC's sound), then `rtcOffer`. The offer's SDP is rewritten (`SdpCandidates`) to advertise
   `Media:PublicIp`:`MediaPort` as a host candidate; the browser's checks come in through the port
   forward and SIPSorcery learns it as peer-reflexive. The offer also carries `BindAddress`,
   ranked first, so the video stays on the LAN when the glasses can reach it (`Media:OfferLan`,
   `Always` by default: the phone relaying the glasses may use mobile data even at home).
4. Control messages (`ControlProtocol.cs` ⇄ `client-web/src/protocol.ts`, keep in sync):
   `setMode`, `setRegion`, `move` (absolute 0..1 in the view, not dx/dy), `click`, `scroll`
   (browser deltaY sign, ≤ 1200 per message), `typeText`, `key` (allowlist), `switchApp`
   (slot 1-9), `setAudio` (`enabled` bool: the ♪ setting, sent after `hello` and on each toggle),
   `ping`, `rtcAnswer`, `iceCandidate`, `stats` (numbers-only allowlist, for the stats
   log; not input for the idle timeout). Server → client: `pairCode`, `paired`,
   `pairFailed`, `authFailed`, `authenticated`, `hello`, `rtcOffer`, `region`, `appSwitch`
   (switched/notRunning/failed), `pong`, `mediaStats` (about once a second: pump timings and
   `[rtp, capturedAtUnixMs, bytes]` for every frame sent).
5. Modes (server-gated in `InputController`): Overview (button labelled **"Region"**; moves the
   region box), **Pointer** (cursor, click, scroll; with Pan on, pushing past an edge pans the region), Type.
   View and Scroll are still in the protocol but have no buttons any more. The region is persisted by
   `RegionStore` across sessions and mirrored to `CastArea`, which the tray draws on the monitor.
6. Geometry is mirrored: `RegionMath.cs` ⇄ `geometry.ts` (letterbox fit, clamp to monitor, min 160 px).
   Frames are always 600×600 with the source letterboxed.
7. **Audio** (`architecture/audio.md`): with `Audio:Enabled` (default) the offer also carries a
   send-only Opus track (stereo, FEC, 40 kbit/s), bundled on port 50000. `AudioPump` (own thread)
   captures every app's sound by WASAPI **process loopback** (before the PC's volume and mute:
   loudness is set on the glasses) and sends 20 ms packets straight out (not through the pacer). Off until the glasses' `setAudio`; off = nothing captured or sent.
8. App shortcuts: `Apps:Shortcuts` in `appsettings.Local.json` (name + process and/or window-title
   text). `switchApp` brings that app's most recent window to the front and fits its visible frame
   to the cast area (`Win32WindowSwitcher`, compensating for invisible resize borders).

## Glasses controls (as tuned on the device)

The mode bar: **Region · Pointer · Type · 1 · 2 … · Pan · ↕n · ☀n · ♪ · Look · Stats · ? · End** (one
row; with two app buttons it has a little room to spare, and the e2e drive checks it fits). **End**
goes back to the PC/Phone first screen (so does End in a phone session), never the ended screen.
**?** (on the phone's bar too) shows the session's gestures and shortcuts under the bar; a second
press closes it. **Keep it current:** any change to a gesture, swipe or Back updates the ? panel in
the same piece of work. Swipe doubles come from `swipes.ts` by themselves; the other rows are text
in `client-web/src/shortcuts.ts` (tested in `shortcuts.test.ts`). Model in
`focusnav.ts`; the app is either on the **view** (swipes act on the desktop) or on the
**controls** (swipes move focus, a pinch presses the focused control).

- **Swipes (PC and phone alike, decided 2026-09-27; `swipes.ts` is the only place they're
  mapped, table in `architecture/glasses-client.md` "Swipes"):** **right twice → Type**; **left
  twice → next app** (PC) / **the app overview** (phone: Recents; swipe left/right, which act at
  once there, pinch to pick, Back leaves; Fit follows the pick); phone only: **down twice →
  Back** (closes the keyboard). Nothing on up twice, so up scrolls at once (user's choices,
  2026-09-29). Doubles within 0.3 s.
  A single swipe that can double waits 0.3 s, then does its plain action (scroll, pan, page); up
  acts at once on both, and down on the PC. Up/down drops a waiting left (the band reads some down-swipes as left).
- **Pinches (PC and phone alike, 2026-09-27; table in `architecture/glasses-client.md` "Pinches"):**
  like a laptop touchpad (user's choice): pinch = click / tap (after 350 ms: two quick ones =
  double-click / double tap); pinch and move = cursor only; **pinch, then pinch and hold (tap-and-
  a-half) = button / finger held**: move to select text, drag a window or drag on the phone; let
  go to release (held still: press and release in place, a long press on the phone). PC:
  `mouseButton` message, released by the PC itself on mode change or session end. (The
  pinch-then-hold → controls gesture and a short-lived "hold still, then move" drag are gone.)
- **Pointer mode (default).** Pinch-drag moves the cursor; pinch clicks (waits 350 ms for a second
  pinch → double-click). Swipes: **up/down scroll** 3 notches (default), the doubles above
  (next app: 1 → 2 → … → 1). The view is **locked** by default: the cursor goes up to
  the edges, and pushing past the top/bottom edge starts **hold-to-scroll** (`edgeScrollStep`):
  steady scrolling until the drag ends or comes back in. It also starts when the glasses' own
  pointer is pushed against the display edge, since it then reports no more movement. **↕** sets
  the scroll strength per app (9/5/3/2/1 notches per swipe, default 3; edge scrolling at half that per
  second), kept in localStorage by app name. The **Pan** toggle makes single swipes move the view by a quarter screen instead, and pushing
  the cursor past an edge slides the view (edge panning).
- **Back** (middle-finger pinch): from the view → the controls (focus on Type from Pointer, Pointer
  otherwise); from the controls, Type or Region → **home to Pointer mode**. (Pinch, then
  pinch-and-hold no longer opens the controls: it's the tap-and-a-half drag.)
- **Mode bar is hidden** (opacity 0, click-through) while on the view; shown when focused, in Type
  and in Region.
- **Type flow (PC and phone, one `TypePanel`):** entering Type focuses and clicks the text box (tries to open the composer at
  once) → when the composer hands text back (`change`), **Send text** → **Enter** → Enter itself
  returns to Pointer mode. Only a swipe of the user's stops the chain. No length limit: long
  text goes as several ≤500-character `typeText` messages (`textChunks`).
- **App buttons:** the current app is highlighted (moves only on a confirmed switch).
- **Mode bar focus wraps round** (2026-09-28): swiping left from its first button lands on the last,
  right from the last on the first. Buttons never wrap their label (`white-space: nowrap`); keep
  labels without spaces (`↕3`, `☀80`), as a space let them break onto two lines on the glasses.
- **☀ brightness** 100/80/65/50 % (default 80 %) on top of the look; kept in localStorage.
  `lifted` is the default look.
- **♪ the PC's sound** (only when `hello.audio`): on by default, kept in localStorage. Off mutes
  at once and stops the PC capturing and sending (bandwidth back to the video); never turned
  off automatically (user's choice, 2026-09-27). The label is always just `♪` (a wider one
  broke the bar): off = no highlight; warning colour = the browser waits for a gesture before
  playing (after a reload), and the next pinch or swipe starts it. Full level whatever the PC's
  volume; set the loudness on the glasses.
- The status bar's yellow text is a "last input" readout, useful for on-device debugging.
  `live (local)` / `live (remote)` says whether the video comes over the LAN or the internet;
  `V n · A n kbps` what video and audio use (payload received; `A off` with ♪ off).
- **Leaving the app ends the session:** hidden for 5 s (`HIDDEN_MS`, Page Visibility), the client
  closes it; a page frozen outright stops pinging and the PC closes it after
  `Session:HeartbeatTimeout` (15 s, any message counts). Either way the cast frame and banner go,
  and the ended screen's Reconnect (focused) resumes with the device token.
- **Stats** (last bar button) shows the latency panel; see "Measuring on the device".

## Measuring on the device (Stats panel and stats log)

- **Panel:** `e2e` = PC capture start → frame shown (avg 2 s, max 10 s, with the slowest frame's
  size), split into `PC→here` (capture, encode, pacing, network) and `buffer+show` (jitter buffer,
  decode, render); `clock ±n` is the clock-offset error. Then the receiver's counters (jitter
  buffer, decode, bitrate, lost, NACK, PLI, freezes, dropped) and the PC's pump figures
  (capture, encode, `send` = wait in the pacer until a frame's last packet left), `PC target n
  kbps · REMB · loss% · resent n of m` (plus `RTCP unreadable n` when the PC couldn't decrypt
  some of the glasses' RTCP), and a `net` line (connection type, ICE network type,
  the browser's bandwidth estimate, UDP rtt), and with sound an `audio` line (kbps, lost,
  concealed ms, jitter buffer, the PC's send rate). `LINK TEST n kbps` heads it during a link test. The
  status bar's `ms` is only the control socket's ping. Not included: the wait for the next capture
  tick (0–50 ms at 20 fps) and the glasses' display scan-out.
- **How it works:** the PC's `mediaStats` lists `[rtp, capturedAtUnixMs, bytes]` per frame sent;
  the client matches RTP timestamps with `requestVideoFrameCallback` and takes the PC clock offset
  from the quickest ping/pong (`mediaStats.ts`).
- **Stats log (read this instead of asking the user to dictate numbers):**
  `%LOCALAPPDATA%\GlassesRemote\stats\stats-yyyy-MM-dd.jsonl`, one JSON line per second per
  side for every session (panel open or not): `kind` = `glasses` (their figures; `framesShown` 0
  means no per-frame timing in that browser; `plis` = keyframe requests it sent, cumulative;
  `netType`/`iceNetType` = codes 1 wifi, 2 cellular, 3 bluetooth, 4 ethernet, 5 vpn, 6 wimax,
  7 other, 8 none, 0 unknown, null = not reported; `downlinkMbps`, `rttMs`; `audioKbps`,
  `audioLost`, `audioConcealedMs` = sound the decoder made up, not counting the PC's DTX silence,
  `audioBufferMs`),
  `pc` (capture/encode/`sendMs` timings, frame KB, keyframes forced and `keyframeRequests` =
  PLI/FIR received, compare with the glasses' `plis`; `nacked` = packets the glasses NACKed,
  `resent` = those sent again, compare with the glasses' `nacks`/`lostTotal`; `rtcpUnreadable` =
  RTCP packets from the glasses SIPSorcery couldn't decrypt, so their NACKs/PLIs/loss went
  unheard (each logged as a warning with source, sender SSRC and the clear SRTCP trailer); `targetKbps` = the
  encoder's adapted target, `rembKbps`/`lossPct` = the glasses' last RTCP estimate and loss,
  `linkTestKbps` = link test step, 0 outside it, `audioOn` 1/0, `audioKbps` Opus payload sent,
  `audioPackets`) and `event` (start with `lanOffered`, mediaPath with `lan` = whether the video
  went over the LAN, setMode, switchApp, setAudio with `on`, end). The e2e harness writes to `%TEMP%\glasses-e2e-stats` instead.
- **Link test (diagnostic):** run the server with `Media__LinkTestOnStart=true` (env var, or
  `Media:LinkTestOnStart` in appsettings.Local.json) and every session starts with ~15 s of a
  noise pattern at 500/1000/2000/4000/8000 kbit/s. Compare the `pc` `kbps` with the glasses'
  `kbps`, `lost` and `arrivalMs` per step. Run it on the glasses and in the phone's browser on
  the same network: that separates the phone-to-glasses hop from the internet path.
- **Keep it accurate.** The user and future agents diagnose from these figures, so a change that
  affects them updates the measurement and this section in the same piece of work:
  - Media pipeline changes (capture, encoder, frame rate, keyframes, pacing, RTP/RTCP handling,
    PLI/NACK, jitter buffer): make sure the panel and log still measure what they claim, and add
    a field when the change introduces something worth watching (e.g. keyframes sent on PLI).
  - Changes to the stats fields: keep `ControlProtocol.ClientStatsFields` ⇄ `StatsReport`
    (`protocol.ts`) ⇄ `statsReport()` (`mediaStats.ts`) in sync, and the `pc` line in
    `ControlSession.SendStats` ⇄ `mediaStats` parsing. Update the field lists above.
  - New user actions that change the picture a lot (app switch, mode, region, anything that
    redraws the whole screen): log them as an `event` so latency spikes can be lined up with them.
  - Only numbers and fixed names ever go into the stats message and log (see the invariants).
  - Re-run the e2e harness: its Stats checks confirm e2e matching and the log still work.

## Security invariants — do not break

- Only public ports: TCP 443 (→ Caddy 8443) and UDP 50000 (phone mode adds none: its media goes
  phone ↔ glasses). App listens on loopback (except the
  dev `lan` profile). RDP, admin endpoints, shells, file APIs: never.
- No PC session without a human clicking **Approve** on the PC, or a device token from such an
  approval less than 24 h ago. (Phone sessions are gated on the phone instead: see Phone mode.) **Never** add auto-approve, a bypass flag, or a network approval
  endpoint to `server/`. Auto-approval exists only in `tools/e2e-harness` (dev-only, 127.0.0.1:5081).
- Approval token: single-use, hashed server-side, constant-time compare, never in URLs, storage,
  React state or logs (test `Tokens_never_appear_in_logs`). Failures are generic (`pairFailed`/`authFailed`).
- Device token (`PairingCoordinator`, `DeviceGrant`; `Pairing:DeviceGrantLifetime`, 24 h, 0 = off):
  256-bit, sent once on the session socket, **swapped for a new one on every use**, only SHA-256
  hashes kept (also in `%LOCALAPPDATA%\GlassesRemote\device-grant.json`). The expiry is fixed at
  approval, never extended. One device remembered at a time; a new approval replaces it.
  Reusing a swapped-out token forgets the device, ends any session and raises `DeviceTokenReused`,
  once the glasses have confirmed the new token (their `rtcAnswer`, which follows `authenticated`);
  until then the old token still resumes, since the connection may have dropped before it arrived.
  A protocol violation or **Forget remembered glasses** in the tray forget it too. Ending a
  session on the PC (tray or Ctrl+Alt+Shift+X) does **not** (user's choice, 2026-09-27): the
  glasses' Reconnect resumes without a new approval. Still one session at a time: a resume only takes over a session of the **same**
  device (closed as `replaced`), never anyone else's, and never while a pairing is pending.
- The client stores only the brightness level, scroll strengths per app name, the ♪ setting, the last target (PC/Phone), the device token (`connection.ts`) and the phone pairing `{id, key}` (`phoneTrust.ts`) (localStorage;
  never in React state, URLs or logs). Reconnecting is a user choice (Reconnect button); after a
  page (re)load one pinch on the first screen (last target focused) resumes.
- Exact Origin allowlist on both sockets; `AllowedHosts`; `Web:AllowSameOrigin` is forced off
  outside Development.
- Strict protocol: unknown types/properties rejected, sizes capped (16 KB msg, 500 chars text),
  coordinates clamped, rate-limited; invalid input closes the session and raises an alert.
- Typed text never presses Enter (newlines are flattened); Enter is a separate key message.
- Sound flows one way only: what the PC plays, to an authenticated session, while the glasses
  have ♪ on (the banner then says "sound on"). No microphone, and nothing from the glasses.
- App shortcuts are configured only on the PC. The glasses send a slot number, never a process
  name or path, and the server only activates and resizes windows that are already open.
  Never launch processes. The e2e harness records switches and must never move real windows.
- Strict CSP (`script-src 'self'`, no inline/eval). Keep the client free of inline scripts/styles
  in `index.html`; React `style` props are fine (they go through the CSSOM).
- **Phone mode:** the **phone is the gate**. Glasses get in only by pairing with the phone (ECDH
  numeric comparison with a commitment: the same code on both screens, **Approve on the phone**)
  and proving the pairing key on every session; the phone's offer and the glasses' answer are
  MACed with the session key. Every phone session also needs Android's screen-capture consent
  tapped **on the phone**. The server only relays that setup (pairing, proof, offer, answer, ICE)
  as opaque strings, holds no key, and never carries the phone's video or the glasses' input for
  it; any input message on a relay is a violation. Relays need no login on the PC, so they're
  rate-limited; the phone prompts to pair at most every 10 s. Never let the server approve,
  store or see a phone pairing key. The companion registers with the PC once through the Approve
  popup (it may then use the PC as a meeting point); its 256-bit token is hashed on the PC
  (`companion-grant.json`), forgettable in the tray. `/ws/companion` refuses any
  request with an `Origin` header (web pages). The companion parses DataChannel input as
  strictly as `ControlProtocol`, and typed text never presses Enter. It only brings back apps
  you've used on the phone (its own recent list, from accessibility; the glasses send
  previous/next, never an app name) and never opens URLs (user's choice, 2026-09-28).
- Approve popup: Reject is the focused/Cancel button, no AcceptButton. The cast-area frame and the
  session banner are excluded from capture (`WDA_EXCLUDEFROMCAPTURE`) and never take focus.

## This machine's deployment (facts, not defaults)

- Hostname `glasses.example.com`, A record at GoDaddy → static public IP **203.0.113.10**.
- Router: external **TCP 443 → 192.168.1.114:8443** (Caddy), **UDP 50000 → 192.168.1.114:50000**.
- **IIS runs on this PC and owns 443 and 80** (http.sys, `CN=localhost` cert). That's why Caddy
  uses `https_port 8443` and the HTTP-01 challenge is disabled. Don't try to take 443 back.
- **Two adapters on the LAN:** Ethernet 192.168.1.114 (forward target) and Wi-Fi 192.168.1.200
  (Windows' preferred outbound route). `Media:BindAddress=192.168.1.114` is required, or UDP
  replies leave via Wi-Fi and video hangs at "connecting".
- `server/appsettings.Local.json` (git-ignored) holds `Media:PublicIp`, `Media:BindAddress` and the
  app shortcuts (1 = Claude: Windows Terminal titled `CHRIS-PC:` where Herdr runs; 2 = Browser:
  `chrome`); see `appsettings.Local.example.json`. It's read at startup only; restart after changes.
- Firewall rules come from `deploy/firewall.ps1` (admin): TCP 8443, UDP 50000, optional TCP 5080 LAN-only.
- Caddy certificate is stored in `%APPDATA%\Caddy`; access log `caddy-access.log` in the repo root
  (git-ignored). The log's User-Agent tells devices apart: `Greatwhite` = the glasses; the phone
  is a Galaxy S25 (SM-S931B, Android 16, One UI 8.0). It's paired for **wireless adb** (`adb devices`
  lists it as 192.168.1.233:<port>; the port changes when wireless debugging restarts). A mobile-network IP there means the real outside path.
- Remote is `origin` = github.com/handzlikchris/GlassesRemote; the user asks for pushes ("check in").

## Gotchas already paid for

- **Media Foundation H.264:** `MFTEnumEx` returns NVENC/QSV hardware MFTs even with the sync flag;
  they're async-only, so filter on `MF_TRANSFORM_FLAGS` & SYNCMFT. Set low-latency mode
  (`MF_LOW_LATENCY` / `CODECAPI_AVLowLatencyMode`) **before** setting media types, or the encoder
  silently buffers every frame. `ICodecAPI` is declared by hand (Vortice doesn't wrap it).
- **Keyframe requests:** SIPSorcery's offer lists only `transport-cc`; `SdpFeedback` adds
  `nack pli`/`ccm fir` or Chrome's PLIs don't come through. It also adds `a=rtcp-rsize`: a few seconds in, Chrome puts a REMB into every
  receiver report, and SIPSorcery keeps only one feedback item per report, so PLIs sent inside a
  report vanished (glasses: 124 sent, 2 heard). With rsize a PLI comes first in its own packet,
  read from the unencrypted SRTCP header. After a mid-stream loss Chrome first NACKs (up to ~28
  times over a few seconds) and only then asks; on a start it asks at once.
- **NACK (retransmission):** plain `nack` is offered for H.264 only (VP8 goes through SIPSorcery's
  SendVideo, which keeps nothing). Resends reuse the original sequence number (no RTX). NACKs
  arrive in packets of their own (rsize), which SIPSorcery drops as unmatched, but it has already
  decrypted the channel's buffer **in place** when our handler runs (it subscribed first), so we
  read them there (`RtcpReadable.Decrypted` checks; one it couldn't decrypt is dropped and counted,
  never decrypted from a copy: that would move SIPSorcery's replay window). SIPSorcery's own NACK
  parse keeps only the first FCI; `RtcpNack` reads them all.
- **Pacing:** H.264 frames are packetized by `H264Rtp` and sent by `RtpPacer` at
  `Media:PacingKbps` (6000) or faster to stay within `MaxPacingDelayMs` (150). Sent in one burst,
  keyframes (35-75 packets) lost ~26% of packets over mobile data. `Thread.Sleep` only wakes on
  the 15.6 ms tick, hence `PreciseSleep` (high-resolution waitable timer). Packets go through
  `VideoStream.SendRtpRaw`, which does SRTP, sequence numbers and the TWCC extension.
- **SIPSorcery:** media port must be **even**; `RTCConfiguration.X_BindAddress` pins the adapter;
  browsers' mDNS `.local` candidates are unusable and ignored; "DTLS packet received … no DTLS
  transport available" warnings at startup are a harmless race.
- **Bitrate adaptation:** `BitrateController` acts on the glasses' RTCP loss only (>10% cut by
  half the loss, <2% +8% while busy). REMB is logged, not used: Chrome caps it at ~1.5x what it
  receives and raises it ~8%/s, so it lags far below what a link carries after a still screen.
  It starts at `Media:StartKbps` (1000). Requested keyframes are at least 1.5 s apart (500 ms fed
  a keyframe storm on a weak link) and NACKed packets are resent up to 3 s back (1 s missed NACKs
  on a queued-up link).
- **SRTP rollover (SIPSorcery):** its sender moves the rollover counter on when it *encrypts*
  sequence 65535, not when the numbers wrap. Resending 65535 moved it on twice and the glasses
  dropped every later packet (video frozen for good, ICE fine). `SentPackets` only resends
  packets of the current epoch; every packet 65535 must be encrypted exactly once, so never skip
  a sequence number without sending it. The e2e harness starts streams at 65495 to cross a wrap.
- **Audio capture follows the PC's volume** with endpoint (default-device) loopback, so the
  glasses heard next to nothing at 22 %: use process loopback (`ProcessLoopbackCapture`), which
  records before the master volume and mute. NAudio implements it internally only.
- **Audio (Opus/WebRTC):** Concentus has no DTX in general-audio (CELT) mode, and plain gaps
  in silence were concealed as loss by Chrome (~400 ms/s "concealed"); the pump sends a 1-byte
  header-only packet when silence starts and every 400 ms instead, as WebRTC does. Chrome decodes
  Opus in mono unless its own answer says `stereo=1` (`withStereoOpus` adds it). Audio and video
  must be in separate msids (`SdpStreams`), or Chrome would delay the video for lip sync.
  SIPSorcery puts the audio m-line first. Audio and the pacer's video share `_sendLock` (one
  SRTP transport); audio packets are never resent or skipped (SRTP rollover, as for video).
  `RtcpReadable` must accept either SSRC, or the glasses' audio reports count as unreadable.
- **Keyframes:** `FramePump` forces them when the source size changes, on request (PLI) and
  every `KeyframeIntervalSeconds` (10 s; 2 s before NACK), not when the region moves; edge
  panning would otherwise send a keyframe every 100 ms. The encoder's own GOP follows the same
  setting (it was a fixed 2 s before 2026-09-27).
- **ASP.NET config:** `Urls` in `appsettings.json` overrides launchSettings `applicationUrl`;
  set `Urls` as an environment variable in a profile instead.
- **Client:** no `<StrictMode>` (double effects would open two sessions for one token);
  `touch-action: none` must be in the initial CSS (pinch-drag); the display is additive, so
  black is transparent and chrome must be bright-on-dark. While panning, the client ignores
  `region` echoes until all its `setRegion`s are answered (`unconfirmedRegions`).
- **Focus on the glasses:** the glasses reset focus (to the first button) right after a Back
  navigation and when the composer closes. Focus the app moves on purpose goes through
  `focusPinned`, which holds it for 600 ms against such resets (a swipe or pinch ends the hold).
  A pinch lands at the glasses' pointer position, so with focus moved by swipes, a pinch on any
  other element presses the focused control (see `onPointerDownCapture`).
- **Console apps in Windows Terminal:** started from the Run box, the terminal window has no
  process link to the app (default-terminal handoff), so match such shortcuts by the title the
  app sets plus `Process: "WindowsTerminal"`.
- **WinForms DPI:** `ApplicationConfiguration.Initialize()` must run first in `Main`
  (PerMonitorV2), so capture bounds, `SendInput` and `SetWindowPos` use physical pixels.
- **e2e harness:** it keeps recorded input for its whole life, so restart it between `drive` runs.
  Press top-bar buttons with `tapBar()` (a plain `el.click()`): the bar is hidden and
  click-through while on the view, and a mouse press after keyboard focus is treated as a pinch
  and redirected to the focused control (by design).
- **Scripts:** keep `.ps1` files ASCII (Windows PowerShell 5.1 misreads UTF-8 without BOM).
- **Editing:** don't write C# string or char literals containing `\u2028`/`\u2029` escapes through
  file-writing tools (they became literal characters once); use `(char)0x2028`. Python rewrites on
  Windows default to CRLF; use `newline=''` to keep LF (`.gitattributes` has `eol=lf`).

## Meta Ray-Ban Display platform facts

- Web apps run in a fixed **600×600** viewport, HTTPS only, D-pad focus + pinch (Neural Band).
  Continuous pinch-drag needs `touch-action: none`. There's no pointer lock.
- Web apps get motion/orientation, phone GPS, Neural Band input and local storage.
  **No camera and no microphone** (`getUserMedia` fails). The glasses camera is only available
  to phone apps through Meta's Wearables Device Access Toolkit.
- Budget guidance: under 300 KB first load and fewer than 15 requests. We're at ~291 KB JS
  (~92 KB gzipped) + 4.5 KB CSS in 3 requests (2026-09-29): close to the line, so watch what
  new code costs (gzipped it's far under).
- **Verified on the device (2026-09-25):** WebRTC H.264 video plays in the glasses WebView, over
  home Wi-Fi and via the phone's mobile data. User agent contains `Greatwhite` (Android 14 WebView).
- **Input as the page sees it:** thumb swipes → `ArrowUp/Down/Left/Right` keydowns; an index pinch →
  a pointer tap **at the glasses' pointer position, not on the focused element**; pinch-drag →
  pointer events. Back (middle-finger pinch) calls `history.back()` when the page has a history
  entry, else opens the system web app menu (Restart/Resume/kill); a session keeps one entry
  (re-pushed after each Back). Double middle pinch toggles the display (reserved).
- The voice/handwriting **composer** is a system feature: the page only gets text via
  `input`/`change` on a focused `<textarea>`; per Meta's docs it opens on user activation, not
  `.focus()` (the app tries a click inside the user's gesture; unconfirmed whether that counts).
- **Caching:** Restart in the web app menu reloads from HTTP cache. The server sends
  `Cache-Control: no-cache` for the page and `immutable` for hashed `/assets` (`ClientCaching`);
  verified: Restart picks up new builds. The pairing screen shows `Build <commit> · <time>`.
- Docs: https://wearables.developer.meta.com/docs/develop/webapps and
  https://github.com/facebook/meta-wearables-webapp

## How the user likes to work

- **Commits:** small, logical, one concern each, conventional prefixes (`feat(server): …`,
  `fix(media): …`, `test(e2e): …`, `docs: …`, `build(deploy): …`), with a body explaining what and
  why. The user reviews via `git log`. Commit as you go, not in one lump at the end. End messages
  with the attribution lines the session provides.
- **Verify before claiming:** run `dotnet test`, the client tests/typecheck/build, and the e2e
  harness for anything touching the flow. Say plainly what wasn't verified (anything needing the
  glasses, the phone, or the router).
- **Iterating on the glasses:** the user tests on the device and reports by voice (expect
  transcription slips: "harder" = Herdr). After each change say the build label to look for and
  whether a server restart is needed. Ask for the yellow readout when behaviour is unclear.
- **Plan artifact:** update in place (same URL), styled HTML with sidebar TOC and callouts; check
  the live version before publishing. Don't resolve the user's comment threads; they do.
- **Ask before system changes** (installs, firewall, IIS/services, stopping their processes).
- Don't commit binaries, `appsettings.Local.json`, logs, or build output (`.gitignore` covers them).

## Status and next steps (as of 2026-09-25)

- **Phone mode (2026-09-27, branch `feat/phone-mode`):** used from the glasses at home
  (`live (local)`: the video comes through Meta's app on the phone). **Redesigned 2026-09-29:**
  pairing and proof on the phone (works on the device), the server only a relay for the first
  connection, sessions that outlive it (pings both ways over the DataChannel). Tried going
  offline the same day: not possible, Meta drops the glasses' link when the phone loses internet
  (details in `architecture/phone-mode.md`, "What it can't survive").

- Works end to end from the glasses and the phone: video, pairing, Pointer, Type with the
  composer, app shortcuts, cast-area frame, brightness. Controls were reworked on the device.
- **Audio (2026-09-27):** works on the glasses: the PC's sound plays through their speakers
  (Web Audio), with their own volume. The first try sounded silent because endpoint loopback
  followed the PC's low volume; process loopback fixed that. Unverified: the gesture fallback
  after a reload.
- Unconfirmed on the device: whether the composer opens automatically on entering Type; the Pan
  toggle (user reported it not working before the always-visible bar; no readout yet).
- Pending: external port scan, decode cost on the glasses, pinch-drag
  gain/threshold tuning. (Pinch-then-hold → controls is gone; the 350 ms click delay stays, for
  double-click and tap-and-a-half.)
- Open question: primary monitor resolution/scaling (affects region defaults and readability).
- **Code review (2026-09-27):** https://claude.ai/artifact/DtG7DX4v6vaqCNYp5wiGuB (read with
  the Artifact tool). F1-F4 fixed the same day (device token confirmed on the first
  `rtcAnswer`; token kept on a refused resume; no NACK copy-decrypt, `rtcpUnreadable` counted and
  logged; `media error` / `server error` close reasons). Open: F5-F14 (low), e.g. the Approve
  popup's stale "End session" text, stale cursor before clicks, region file written per pan step.
- **Task briefs for new sessions live in `.claude/tasks/`.** Start there when asked to "pick up
  the task". `square-screen-keyboard.md` (2026-09-29, idea): a smaller keyboard on the phone's square screen
  (switch keyboards with Square/Reset, or an invisible keyboard in the companion).
  `phone-direct-dat.md` (2026-09-29): a native DAT glasses app instead of the web app was
  **looked at and not pursued** (no live video in DAT Display; the companion already is the
  native app); lists extras for later (mic dictation, cards, buttons, camera). `companion-sensor-bridge.md` (phone companion app relaying the glasses' camera and
  mic via Meta's DAT) is planned but **on hold** at the user's request.
- **Latency over mobile data (2026-09-27):** the session showed ~26% packet loss, all in the
  seconds keyframes went out, and only 1 in 5 frames shown. Pacing (`RtpPacer`) and heard PLIs
  (`rtcp-rsize`) cut the loss to ~4% on the glasses, with 1-2 s stalls left when a keyframe lost
  packets. With NACK and 10 s keyframes the next session collapsed when the glasses' link
  carried only ~0.8 Mbit/s (the phone measures ~50 Mbit/s on 5G): video queued up to 1.4 s,
  resends missed, keyframe storm. Suspect: the phone-to-glasses hop (Bluetooth?) when the phone
  isn't on Wi-Fi. Added: network type logging, a link test, loss-based bitrate adaptation,
  1.5 s keyframe gap, 3 s resend window. First link test on the glasses (5G): ~0.9 Mbit/s
  delivered, delay growing from the 2000 step, `netType` 8 (none) and `iceNetType` 0: the
  glasses' WebView sees no network of its own (traffic relayed by the phone). That run froze
  for good through the SRTP rollover bug (fixed). Phone-browser comparison still to do. Still open: a delay
  signal for the controller (the glasses' arrivalMs, or RTT), gradual intra refresh or a
  keyframe size cap, and as a last resort a TCP path (WebSocket + WebCodecs), like RDP.
- **Open bug (2026-09-27, 11:30 session, glasses on the slow relay):** ~13 s in, SIPSorcery
  started rejecting every SRTCP packet from the glasses (4,590 × "SRTCP unprotect failed",
  result -4 = replay, after one -3 = HMAC failure; also STUN integrity failures). With the
  glasses' RTCP unreadable, NACKs weren't resent (0 of 3,728) and the bitrate target froze at
  479 kbit/s. Not seen in the harness. Next: log each raw SRTCP index (trailer, in the clear) to
  see duplicates/corruption/jumps, then recover (reset SIPSorcery's SRTCP replay state, or
  renegotiate) instead of staying blind. The morning's link test logged "no RTCP activity for
  30 s" too, so it may have hit this as well. Checked in SIPSorcery v10.0.16's source
  (`SrtpContext.UnprotectRtcp`): HMAC is verified before the replay check and the window only
  advances after authentication, so one corrupt packet can't poison it. Something that
  authenticated with the live keys moved the window ahead (or the glasses' index went back).
  Suspects: our `OnNack` copy-decrypt path, which updates the same replay state as
  SIPSorcery's own decrypt; an index jump in the WebView. The STUN integrity failures hint at
  a stale peer also sending to 50000 (fits the one -3, can't move the window). Since review
  fix F3 the copy-decrypt is gone, and each undecryptable RTCP packet is counted
  (`rtcpUnreadable`) and logged with source, sender SSRC and trailer (E flag + index): read those
  after the next relay session.
- Ideas queued: live PC frame while dragging in Region; "video not connecting" hint after ~15 s;
  phone-friendly layout; hardware H.264 (async NVENC/QSV MFT); Windows.Graphics.Capture; TURN
  over TLS for UDP-blocking networks; remote approval flow; Claude-specific controls; a tray
  dialog for app shortcuts.
