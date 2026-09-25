# CLAUDE.md — Glasses Remote Desktop

Guide for AI agents (and humans) picking up this repo. Read this first, then `README.md`.

## What this is

A proof of concept that lets a **Meta Ray-Ban Display** web app control this Windows PC:
view a region of the primary monitor over WebRTC video, move/click/scroll the mouse with
Neural Band gestures, switch between configured apps, and type through the glasses'
voice/handwriting composer. Every session needs a **single-use pairing approved in a popup on the PC**,
or (for 24 h after such an approval) the **device token** of the glasses that were approved.

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
  Pairing/              PairingCoordinator (state machine), Secrets (tokens/codes), SlidingWindowLimiter
  Sessions/             ControlSession (one authenticated session), InputController (mode gating),
                        SocketIO (capped WS reads/writes), TokenBucket
  Protocol/             ControlMessages + ControlProtocol (strict allowlist parser)
  Desktop/              interfaces (IScreen, ICaptureSource, IInputInjector, IKeepAwake, IWindowSwitcher),
                        RegionMath, RegionStore, CastArea (region of the active session), AppShortcuts
  Media/                FramePump, SipsorceryMediaPeer, SdpCandidates, MfH264Encoder, Nv12, Vp8 encoder,
                        StatsLog (daily JSONL of glasses + PC media figures)
  Windows/              Win32 implementations: SendInput, GDI capture, keep-awake, Win32WindowSwitcher,
                        NativeMethods
  Ui/                   TrayApp, ApprovePopup, AlertsForm, SessionBanner, CastFrame (orange frame
                        around the cast area on the PC monitor), TerminateHotkey
  Alerts/               AlertLog, AlertThrottle
client-web/             glasses client (600×600)
  build-label.mjs       stamps "Build <commit> · <time>" into the bundle (shown on the pairing screen)
  src/connection.ts     pair + session sockets; token lives ONLY here, in memory
  src/rtc.ts            receive-only RTCPeerConnection + stats
  src/SessionScreen.tsx modes, gestures, focus handling, Back/history, overlay, edge panning
  src/TypePanel.tsx     text box for the composer, Send text, shortcut keys, focus chain
  src/focusnav.ts       navigation model: swipe actions, Back targets, tap routing (pure, tested)
  src/pinchPress.ts     outside a session (pairing/ended screens), a pinch presses the focused button
  src/mediaStats.ts     Stats panel: capture-to-display latency (RTP timestamp matching, clock offset
                        from ping/pong), receiver counters, PC pump figures (pure, tested)
  src/{protocol,geometry,gestures,controls,display}.ts  pure logic with *.test.ts
tests/                  xUnit: unit + WebSocket integration (TestServerHost) + real H.264 encoder
tools/e2e-harness/      DEV-ONLY host (auto-approves pairing, records input and app switches) +
                        browser/drive.mjs (headless Chrome, 34 checks)
spikes/webrtc/          M0 spike: unauthenticated test pattern, timestamp barcode latency meter
deploy/                 Caddyfile, Caddyfile.spike, firewall.ps1
scripts/run.ps1         builds client if needed, runs server (-Dev, -Lan)
tools/bin/caddy.exe     local Caddy binary (git-ignored)
.claude/tasks/          task briefs for new sessions
```

## Commands

```powershell
dotnet build GlassesRemote.sln
dotnet test                                     # ~121 server tests, ~11 s
cd client-web; npm test; npx tsc --noEmit; npm run build   # ~61 client tests, typecheck, dist/
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
3. Server sends `hello` (monitor, region, starting mode = **pointer**, codec, app shortcut names),
   then `rtcOffer`. The offer's SDP is rewritten (`SdpCandidates`) to advertise
   `Media:PublicIp`:`MediaPort` as a host candidate; the browser's checks come in through the port
   forward and SIPSorcery learns it as peer-reflexive.
4. Control messages (`ControlProtocol.cs` ⇄ `client-web/src/protocol.ts`, keep in sync):
   `setMode`, `setRegion`, `move` (absolute 0..1 in the view, not dx/dy), `click`, `scroll`
   (browser deltaY sign, ≤ 1200 per message), `typeText`, `key` (allowlist), `switchApp`
   (slot 1-9), `ping`, `rtcAnswer`, `iceCandidate`, `stats` (numbers-only allowlist, for the stats
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
7. App shortcuts: `Apps:Shortcuts` in `appsettings.Local.json` (name + process and/or window-title
   text). `switchApp` brings that app's most recent window to the front and fits its visible frame
   to the cast area (`Win32WindowSwitcher`, compensating for invisible resize borders).

## Glasses controls (as tuned on the device)

The mode bar: **Region · Pointer · Type · 1 · 2 … · Pan · ↕ n · ☀ n% · Look · Stats** (one row;
tight padding, check a screenshot when adding buttons). Model in
`focusnav.ts`; the app is either on the **view** (swipes act on the desktop) or on the
**controls** (swipes move focus, a pinch presses the focused control).

- **Pointer mode (default).** Pinch-drag moves the cursor; pinch clicks (waits 350 ms for a second
  pinch → double-click). Swipes are shortcuts: **up/down scroll** 9 notches, **right → Type**,
  **left → next app** (1 → 2 → … → 1). The view is **locked** by default: the cursor goes up to
  the edges, and pushing past the top/bottom edge starts **hold-to-scroll** (`edgeScrollStep`):
  steady scrolling until the drag ends or comes back in. It also starts when the glasses' own
  pointer is pushed against the display edge, since it then reports no more movement. **↕** sets
  the scroll strength per app (9/5/3/2/1 notches per swipe; edge scrolling at half that per
  second), kept in localStorage by app name. The **Pan** toggle makes swipes move the view by a quarter screen instead, and pushing
  the cursor past an edge slides the view (edge panning).
- **Back** (middle-finger pinch): from the view → the controls (focus on Type from Pointer, Pointer
  otherwise); from the controls, Type or Region → **home to Pointer mode**. Pinch, then
  pinch-and-hold 0.5 s (movement ignored) also opens the controls.
- **Mode bar is hidden** (opacity 0, click-through) while on the view; shown when focused, in Type
  and in Region.
- **Type flow:** entering Type focuses and clicks the text box (tries to open the composer at
  once) → when the composer hands text back (`change`), **Send text** → **Enter** → Enter itself
  returns to Pointer mode. Only a swipe of the user's stops the chain.
- **App buttons:** the current app is highlighted (moves only on a confirmed switch).
- **☀ brightness** 100/80/65/50 % (default 80 %) on top of the look; kept in localStorage.
  `lifted` is the default look.
- The status bar's yellow text is a "last input" readout, useful for on-device debugging.
- **Stats** (last bar button) shows the latency panel; see "Measuring on the device".

## Measuring on the device (Stats panel and stats log)

- **Panel:** `e2e` = PC capture start → frame shown (avg 2 s, max 10 s, with the slowest frame's
  size), split into `PC→here` (capture, encode, send, network) and `buffer+show` (jitter buffer,
  decode, render); `clock ±n` is the clock-offset error. Then the receiver's counters (jitter
  buffer, decode, bitrate, lost, NACK, PLI, freezes, dropped) and the PC's pump figures. The
  status bar's `ms` is only the control socket's ping. Not included: the wait for the next capture
  tick (0–50 ms at 20 fps) and the glasses' display scan-out.
- **How it works:** the PC's `mediaStats` lists `[rtp, capturedAtUnixMs, bytes]` per frame sent;
  the client matches RTP timestamps with `requestVideoFrameCallback` and takes the PC clock offset
  from the quickest ping/pong (`mediaStats.ts`).
- **Stats log (read this instead of asking the user to dictate numbers):**
  `%LOCALAPPDATA%\GlassesRemote\stats\stats-yyyy-MM-dd.jsonl`, one JSON line per second per
  side for every session (panel open or not): `kind` = `glasses` (their figures; `framesShown` 0
  means no per-frame timing in that browser), `pc` (pump timings, frame KB, keyframes sent and
  `keyframeRequests` = PLI/FIR received) and `event` (start,
  setMode, switchApp, end). The e2e harness writes to `%TEMP%\glasses-e2e-stats` instead.
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

- Only public ports: TCP 443 (→ Caddy 8443) and UDP 50000. App listens on loopback (except the
  dev `lan` profile). RDP, admin endpoints, shells, file APIs: never.
- No session without a human clicking **Approve** on the PC, or a device token from such an
  approval less than 24 h ago. **Never** add auto-approve, a bypass flag, or a network approval
  endpoint to `server/`. Auto-approval exists only in `tools/e2e-harness` (dev-only, 127.0.0.1:5081).
- Approval token: single-use, hashed server-side, constant-time compare, never in URLs, storage,
  React state or logs (test `Tokens_never_appear_in_logs`). Failures are generic (`pairFailed`/`authFailed`).
- Device token (`PairingCoordinator`, `DeviceGrant`; `Pairing:DeviceGrantLifetime`, 24 h, 0 = off):
  256-bit, sent once on the session socket, **swapped for a new one on every use**, only SHA-256
  hashes kept (also in `%LOCALAPPDATA%\GlassesRemote\device-grant.json`). The expiry is fixed at
  approval, never extended. One device remembered at a time; a new approval replaces it.
  Reusing a swapped-out token forgets the device, ends any session and raises `DeviceTokenReused`.
  Ending a session on the PC, a protocol violation, or **Forget remembered glasses** in the tray
  forget it too. Still one session at a time: a resume only takes over a session of the **same**
  device (closed as `replaced`), never anyone else's, and never while a pairing is pending.
- The client stores only the brightness level, scroll strengths per app name, and the device token (`connection.ts`, localStorage;
  never in React state, URLs or logs). Reconnecting is a user choice (Reconnect button); only a
  page (re)load resumes by itself.
- Exact Origin allowlist on both sockets; `AllowedHosts`; `Web:AllowSameOrigin` is forced off
  outside Development.
- Strict protocol: unknown types/properties rejected, sizes capped (16 KB msg, 500 chars text),
  coordinates clamped, rate-limited; invalid input closes the session and raises an alert.
- Typed text never presses Enter (newlines are flattened); Enter is a separate key message.
- App shortcuts are configured only on the PC. The glasses send a slot number, never a process
  name or path, and the server only activates and resizes windows that are already open.
  Never launch processes. The e2e harness records switches and must never move real windows.
- Strict CSP (`script-src 'self'`, no inline/eval). Keep the client free of inline scripts/styles
  in `index.html`; React `style` props are fine (they go through the CSSOM).
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
  is a Galaxy S25 (SM-S931B, Android 16). A mobile-network IP there means the real outside path.
- Remote is `origin` = github.com/handzlikchris/GlassesRemote; the user asks for pushes ("check in").

## Gotchas already paid for

- **Media Foundation H.264:** `MFTEnumEx` returns NVENC/QSV hardware MFTs even with the sync flag;
  they're async-only, so filter on `MF_TRANSFORM_FLAGS` & SYNCMFT. Set low-latency mode
  (`MF_LOW_LATENCY` / `CODECAPI_AVLowLatencyMode`) **before** setting media types, or the encoder
  silently buffers every frame. `ICodecAPI` is declared by hand (Vortice doesn't wrap it).
- **Keyframe requests:** SIPSorcery's offer lists only `transport-cc`; `SdpFeedback` adds
  `nack pli`/`ccm fir` or Chrome's PLIs don't come through. Plain `nack` stays out (no
  retransmission). Chrome asks for keyframes when a stream starts undecodable, but after a
  mid-stream loss it waits for the next keyframe instead (that needs NACK + retransmission).
- **SIPSorcery:** media port must be **even**; `RTCConfiguration.X_BindAddress` pins the adapter;
  browsers' mDNS `.local` candidates are unusable and ignored; "DTLS packet received … no DTLS
  transport available" warnings at startup are a harmless race.
- **Keyframes:** `FramePump` forces them only when the source size changes (and every 2 s),
  not when the region moves; edge panning would otherwise send a keyframe every 100 ms.
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
- Budget guidance: under 300 KB first load and fewer than 15 requests. We're at ~247 KB JS
  (~78 KB gzipped) + 3 KB CSS in 3 requests.
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

- Works end to end from the glasses and the phone: video, pairing, Pointer, Type with the
  composer, app shortcuts, cast-area frame, brightness. Controls were reworked on the device.
- Unconfirmed on the device: whether the composer opens automatically on entering Type; the Pan
  toggle (user reported it not working before the always-visible bar; no readout yet).
- Pending: M0 latency numbers, external port scan, decode cost on the glasses, pinch-drag
  gain/threshold tuning, maybe removing the pinch-then-hold gesture (Back replaced it; dropping
  it would remove the 350 ms click delay).
- Open question: primary monitor resolution/scaling (affects region defaults and readability).
- **Task briefs for new sessions live in `.claude/tasks/`.** Start there when asked to "pick up
  the task". `companion-sensor-bridge.md` (phone companion app relaying the glasses' camera and
  mic via Meta's DAT) is planned but **on hold** at the user's request.
- Ideas queued: live PC frame while dragging in Region; "video not connecting" hint after ~15 s;
  phone-friendly layout; hardware H.264 (async NVENC/QSV MFT); Windows.Graphics.Capture; TURN
  over TLS for UDP-blocking networks; remote approval flow; Claude-specific controls; a tray
  dialog for app shortcuts.
