# CLAUDE.md — Glasses Remote Desktop

Guide for AI agents (and humans) picking up this repo. Read this first, then `README.md`.

## What this is

A proof of concept that lets a **Meta Ray-Ban Display** web app control this Windows PC:
view a region of the primary monitor over WebRTC video, move/click/scroll the mouse with
Neural Band pinch-drags, and type through the glasses' voice/handwriting composer. Every
session needs a **single-use pairing approved in a popup on the PC**.

- **Plan / design / decisions / setup / status** live in a shared page:
  https://claude.ai/artifact/UUBNEYcPv88tssxSezHPVV (read it with the Artifact tool, action `read`).
  Update it **in place** when decisions change. Never create a new plan artifact.
- Milestones: M0 network spike, M1 pairing, M2 streaming, M3 input, M4 hardening (all built),
  M5 validation on the glasses (waiting for the device).

## Stack

| Part | Tech |
| --- | --- |
| Server | .NET 10 (`net10.0-windows`, SDK pinned in `global.json`), ASP.NET Core + WinForms, one process as the logged-in user |
| WebRTC | SIPSorcery 10.0.16, send-only video, fixed UDP port, no STUN/TURN |
| Video | H.264 via Windows Media Foundation software encoder (Vortice.MediaFoundation 3.8.3); VP8 (libvpx via SIPSorceryMedia.Encoders) fallback |
| Capture / input | GDI `CopyFromScreen`; `SendInput` (P/Invoke) |
| Client | React 19, Vite 8 (Rolldown), TypeScript 7 (native `tsc`), Vitest 4 |
| Proxy | Caddy 2.11 (stock) terminates TLS, Let's Encrypt via TLS-ALPN |

## Repo map

```
server/                 GlassesRemote.Server (ASP.NET Core + WinForms)
  Program.cs            STA Main: DPI init, web host in background, tray on UI thread
  Hosting/              ServerApp.Create (DI + pipeline, shared with tests), GlassesEndpoints
                        (/health, /ws/pair, /ws/session, OriginPolicy), SecurityHeaders, Options
  Pairing/              PairingCoordinator (state machine), Secrets (tokens/codes), SlidingWindowLimiter
  Sessions/             ControlSession (one authenticated session), InputController (mode gating),
                        SocketIO (capped WS reads/writes), TokenBucket
  Protocol/             ControlMessages + ControlProtocol (strict allowlist parser)
  Desktop/              interfaces (IScreen, ICaptureSource, IInputInjector, IKeepAwake), RegionMath, RegionStore
  Media/                FramePump, SipsorceryMediaPeer, SdpCandidates, MfH264Encoder, Nv12, Vp8 encoder
  Windows/              Win32 implementations: SendInput, GDI capture, keep-awake, NativeMethods
  Ui/                   TrayApp, ApprovePopup, AlertsForm, SessionBanner, CastFrame (cast area
                        drawn on the PC monitor, fed by Desktop/CastArea), TerminateHotkey
  Alerts/               AlertLog, AlertThrottle
client-web/             glasses client (600×600)
  src/connection.ts     pair + session sockets; token lives ONLY here, in memory
  src/rtc.ts            receive-only RTCPeerConnection + stats
  src/SessionScreen.tsx modes, gestures, overlay, edge panning, region echo handling
  src/{protocol,geometry,gestures,controls}.ts  pure logic with *.test.ts
tests/                  xUnit: unit + WebSocket integration (TestServerHost) + real H.264 encoder
tools/e2e-harness/      DEV-ONLY host (auto-approves pairing, records input) + browser/drive.mjs
spikes/webrtc/          M0 spike: unauthenticated test pattern, timestamp barcode latency meter
deploy/                 Caddyfile, Caddyfile.spike, firewall.ps1
scripts/run.ps1         builds client if needed, runs server (-Dev, -Lan)
tools/bin/caddy.exe     local Caddy binary (git-ignored)
```

## Commands

```powershell
dotnet build GlassesRemote.sln
dotnet test                                     # ~104 server tests, ~11 s
cd client-web; npm test; npx tsc --noEmit; npm run build   # client tests, typecheck, dist/
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
`dotnet test tests/GlassesRemote.Server.Tests.csproj -p:OutDir=E:/_src-unity/MetaDisplayRDP/tests/bin/isolated/`.
Don't stop the user's server without asking. Don't run the e2e harness or spike on port 50000
while it may be in a session (both bind UDP 50000; use `Spike__MediaPort=50002` etc. for side tests).

## Architecture in one screen

```
glasses/phone ──HTTPS+WSS──► router :443 ──► Caddy :8443 ──► 127.0.0.1:5080 server ──► SendInput
      ▲                                                          │ GDI capture → H.264 → SIPSorcery
      └──────────── WebRTC video: UDP 203.0.113.10:50000 ◄───────┘ (router → 192.168.1.114:50000)
```

1. `/ws/pair`: server opens a request (6-char code), the tray shows the **ApprovePopup**; Approve
   sends a 256-bit token down that pair socket only.
2. `/ws/session`: first message must be `{type:"authenticate",token}` within 3 s; token is
   single-use, SHA-256 stored, consumed on use, dies with the session.
3. Server sends `hello` (monitor, region), then `rtcOffer`. The offer's SDP is rewritten
   (`SdpCandidates`) to advertise `Media:PublicIp`:`MediaPort` as a host candidate; the browser's
   checks come in through the port forward and SIPSorcery learns it as peer-reflexive.
4. Control messages (`ControlProtocol.cs` ⇄ `client-web/src/protocol.ts`, keep in sync):
   `setMode`, `setRegion`, `move` (absolute 0..1 in the view, not dx/dy), `click`, `scroll`
   (browser deltaY sign), `typeText`, `key` (allowlist), `switchApp` (slot 1-9), `ping`,
   `rtcAnswer`, `iceCandidate`. Server → client: `pairCode`, `paired`, `pairFailed`,
   `authFailed`, `authenticated`, `hello` (incl. app names), `rtcOffer`, `region`, `appSwitch`, `pong`.
5. Modes give pinch-drag one meaning: Overview (move region box), View, Pointer (cursor; short
   pinch = click; pushing past an edge pans the region), Scroll, Type. `InputController` ignores
   input outside its mode. Sessions start in **Pointer** (the server says so in `hello`); the
   region is persisted by `RegionStore` across sessions.
7. Glasses navigation (`client-web/src/focusnav.ts`, `gestures.ts`): in View/Pointer/Scroll,
   swipes (arrow keys) move the view by a quarter screen; **pinch, then pinch-and-hold 0.5 s**
   (movement ignored) jumps to the mode bar (Type from Pointer, Pointer from View/Scroll). On
   the controls a pinch presses the focused control wherever it lands. Pointer-mode clicks wait
   350 ms for a possible second pinch (double-click). Type walks focus: text box → Send text
   (on the composer's `change`) → Enter → Pointer.
6. Geometry is mirrored: `RegionMath.cs` ⇄ `geometry.ts` (letterbox fit, clamp to monitor, min 160 px).
   Frames are always 600×600 with the source letterboxed.

## Security invariants — do not break

- Only public ports: TCP 443 (→ Caddy 8443) and UDP 50000. App listens on loopback (except the
  dev `lan` profile). RDP, admin endpoints, shells, file APIs: never.
- No session without a human clicking **Approve** on the PC. **Never** add auto-approve, a
  bypass flag, or a network approval endpoint to `server/`. Auto-approval exists only in
  `tools/e2e-harness` (dev-only, 127.0.0.1:5081).
- Token: single-use, hashed server-side, constant-time compare, never in URLs, storage, React
  state or logs (test `Tokens_never_appear_in_logs`). Failures are generic (`pairFailed`/`authFailed`).
- Exact Origin allowlist on both sockets; `AllowedHosts`; `Web:AllowSameOrigin` is forced off
  outside Development.
- Strict protocol: unknown types/properties rejected, sizes capped (16 KB msg, 500 chars text),
  coordinates clamped, rate-limited; invalid input closes the session and raises an alert.
- Typed text never presses Enter (newlines are flattened); Enter is a separate key message.
- App shortcuts are configured only on the PC (`Apps:Shortcuts` in appsettings.Local.json). The
  glasses send a slot number, never a process name or path, and the server only activates and
  resizes windows that are already open (`Win32WindowSwitcher`). Never launch processes.
- Strict CSP (`script-src 'self'`, no inline/eval). Keep the client free of inline scripts/styles
  in `index.html`; React `style` props are fine.
- Approve popup: Reject is the focused/Cancel button, no AcceptButton.

## This machine's deployment (facts, not defaults)

- Hostname `glasses.example.com`, A record at GoDaddy → static public IP **203.0.113.10**.
- Router: external **TCP 443 → 192.168.1.114:8443** (Caddy), **UDP 50000 → 192.168.1.114:50000**.
- **IIS runs on this PC and owns 443 and 80** (http.sys, `CN=localhost` cert). That's why Caddy
  uses `https_port 8443` and the HTTP-01 challenge is disabled. Don't try to take 443 back.
- **Two adapters on the LAN:** Ethernet 192.168.1.114 (forward target) and Wi-Fi 192.168.1.200
  (Windows' preferred outbound route). `Media:BindAddress=192.168.1.114` is required, or UDP
  replies leave via Wi-Fi and video hangs at "connecting".
- `server/appsettings.Local.json` (git-ignored) holds `Media:PublicIp` and `Media:BindAddress`;
  see `appsettings.Local.example.json`. It's read at startup only; restart after changes.
- Firewall rules come from `deploy/firewall.ps1` (admin): TCP 8443, UDP 50000, optional TCP 5080 LAN-only.
- Caddy certificate is stored in `%APPDATA%\Caddy`; access log `caddy-access.log` in the repo root (git-ignored).

## Gotchas already paid for

- **Media Foundation H.264:** `MFTEnumEx` returns NVENC/QSV hardware MFTs even with the sync flag;
  they're async-only, so filter on `MF_TRANSFORM_FLAGS` & SYNCMFT. Set low-latency mode
  (`MF_LOW_LATENCY` / `CODECAPI_AVLowLatencyMode`) **before** setting media types, or the encoder
  silently buffers every frame. `ICodecAPI` is declared by hand (Vortice doesn't wrap it).
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
- **WinForms DPI:** `ApplicationConfiguration.Initialize()` must run first in `Main`
  (PerMonitorV2), so capture bounds and `SendInput` use physical pixels.
- **Scripts:** keep `.ps1` files ASCII (Windows PowerShell 5.1 misreads UTF-8 without BOM).
- **Editing:** don't write C# string or char literals containing `\u2028`/`\u2029` escapes through
  file-writing tools (they became literal characters once); use `(char)0x2028`.

## Meta Ray-Ban Display platform facts

- Web apps run in a fixed **600×600** viewport, HTTPS only, D-pad focus + pinch (Neural Band).
  Continuous pinch-drag needs `touch-action: none`. There's no pointer lock.
- Web apps get motion/orientation, phone GPS, Neural Band input and local storage.
  **No camera and no microphone** (`getUserMedia` fails). The glasses camera is only available
  to phone apps through Meta's Wearables Device Access Toolkit.
- Budget guidance: under 300 KB first load and fewer than 15 requests. We're at ~237 KB JS
  (~74 KB gzipped) + 3 KB CSS in 3 requests.
- **Verified on the device (2026-09-25):** WebRTC H.264 video plays in the glasses WebView, over
  home Wi-Fi and via the phone's mobile data. User agent contains `Greatwhite` (Android 14 WebView).
- **Input as the page sees it:** thumb swipes → `ArrowUp/Down/Left/Right`; an index pinch → a
  pointer tap **at the glasses' pointer position, not on the focused element** (so the app
  redirects it to the focused control); pinch-drag → pointer events. Middle-finger pinch opens
  the system web app menu (Restart/Resume/kill) and double middle pinch toggles the display:
  both reserved. Back (Escape) is not reachable in practice.
- The voice/handwriting **composer** is a system feature: the page only gets text via
  `input`/`change` on a focused `<textarea>`; it opens on user activation, not `.focus()`.
- **Caching:** Restart in the web app menu reloads from HTTP cache. The server sends
  `Cache-Control: no-cache` for the page and `immutable` for hashed `/assets` (`ClientCaching`).
  A page cached before that header existed only clears by removing and re-adding the web app.
  The pairing screen shows `Build <commit> · <time>` to confirm what's loaded.
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
- **Plan artifact:** update in place (same URL), styled HTML with sidebar TOC and callouts; check
  the live version before publishing. Don't resolve the user's comment threads; they do.
- **Ask before system changes** (installs, firewall, IIS/services, stopping their processes).
- Don't commit binaries, `appsettings.Local.json`, logs, or build output (`.gitignore` covers them).

## Status and next steps (as of 2026-09-25)

- Remote access works end to end over HTTPS, from the glasses and the phone (M5 largely done):
  video, pairing, Pointer, Type with the composer. Controls were reworked on the device for
  latency (swipes pan, pinch-hold menu, focus chains); `lifted` is the default look.
- The PC draws the cast area as an orange frame (tray toggle), excluded from capture.
- Pending: M0 latency numbers, external port scan, decode cost on the glasses, pinch-drag
  gain/threshold tuning. The yellow "last input" readout in the status bar is a debugging aid.
- Open question: primary monitor resolution/scaling (affects region defaults and readability).
- **Task briefs for new sessions live in `.claude/tasks/`.** Start there when asked to "pick up
  the task". Current: `companion-sensor-bridge.md` (phone companion app relaying the glasses'
  camera and mic via Meta's Device Access Toolkit, since web apps get no camera or mic).
- Ideas queued: "video not connecting" hint after ~15 s; phone-friendly layout for testing;
  hardware H.264 (async NVENC/QSV MFT); Windows.Graphics.Capture; TURN over TLS for UDP-blocking
  networks; remote approval flow; Claude-specific controls.
