# Glasscast

**Phone remote control for Meta Ray-Ban Display glasses.** Tech preview.

https://github.com/user-attachments/assets/8f886138-fdfd-4640-8a2e-975cf590e1ee

See your Android phone's screen in the glasses and control it from there: Neural Band pinches
and swipes tap, scroll and drag, and the glasses' voice or handwriting composer types. A small
companion app on the phone streams its screen over WebRTC and turns the glasses' input into
taps through an accessibility service. The phone is the gate: new glasses pair on the phone
(the same code on both screens, Approve on the phone), and Android asks for screen-capture
consent on the phone every session. The phone's screen stays on and unlocked while a session is
live. Design: [`architecture/phone-mode.md`](architecture/phone-mode.md); the app:
[`android-companion/`](android-companion/README.md).

**PC mode** does the same for a Windows desktop, remote-desktop style: the glasses view a region
of the primary monitor over low-latency WebRTC video (H.264) with the PC's sound, and drive the
mouse and keyboard. A single-use pairing, approved in a popup on the PC, gates every session;
for 24 h after an approval the same glasses can reconnect with a rotating device token.

Per-feature design notes live in [`architecture/`](architecture/README.md).

The shared plan page has the design, decisions, risks and the router/DNS setup.
This README covers the code.

## Layout

| Folder | What it is |
| --- | --- |
| `server/` | ASP.NET Core + WinForms app, runs as the logged-in user: pairing, WebRTC, capture, input, tray, approve popup |
| `client-web/` | Vite + React + TypeScript glasses client (600×600) |
| `android-companion/` | Phone companion app (Kotlin): screen capture, WebRTC, accessibility input |
| `tests/` | Server tests (xUnit): unit, WebSocket integration, real H.264 encoder |
| `tools/e2e-harness/` | Dev-only harness + headless-Chrome script that drives the whole flow |
| `deploy/` | Caddyfile and the Windows firewall script |
| `scripts/` | `run.ps1`: build the client and start the server |

## How it fits together

```
glasses / laptop ──HTTPS+WSS :443──► router ──► Caddy :8443 ──► 127.0.0.1:5080 .NET server ──► SendInput
        ▲                                         │   GDI capture → H.264 (Media Foundation)
        └───────────── WebRTC video, UDP 50000 ◄──┘   (VP8 fallback)
```

- **Pairing** (`/ws/pair`): the glasses get a 6-character code; a popup on the PC shows
  the same code, and **Approve** sends a single-use 256-bit token to that socket only.
- **Session** (`/ws/session`): the first message must be `authenticate` (approval token)
  or `resume` (device token) within 3 s. Then: `authenticated`, `hello` (monitor, region,
  mode, app shortcuts), the WebRTC offer, and control messages.
- **Media**: SIPSorcery on the fixed UDP port 50000. The offer advertises the router's
  public IP, so the glasses connect straight through the port forward, with no STUN/TURN.
- **Modes** give pinch-drag one meaning at a time: Region (move the region box),
  Pointer (the default: drag moves the cursor, a pinch clicks, swipes scroll), Type.

## Requirements

- Windows 10/11 (capture, SendInput and the tray are Windows-only)
- .NET 10 SDK (LTS, supported to Nov 2028; pinned by `global.json`)
- Node 20+ for the client
- Caddy (stock build) for public HTTPS

## Run locally (no Caddy, no glasses)

Same PC, any browser:

```powershell
.\scripts\run.ps1 -Dev
# open http://127.0.0.1:5080 (600×600 is the glasses' size)
# approve the popup on the PC; the tray icon turns green
```

Another device on your home network (e.g. the laptop):

```powershell
.\deploy\firewall.ps1 -LanTesting   # once, admin PowerShell: TCP 5080 from your local subnet only
.\scripts\run.ps1 -Lan              # prints the address to open, e.g. http://192.168.1.114:5080
```

From Rider or `dotnet run`, use the **lan** launch profile. It listens on all interfaces
(`Urls=http://0.0.0.0:5080`, which overrides `appsettings.json`), accepts any host name, and
turns on `Web:AllowSameOrigin`. That last setting lets the page open the WebSockets from
whatever LAN address served it, and it is ignored outside Development.

Plain HTTP on the LAN is for testing only; the router doesn't forward 5080.

## Run for real

1. Router, DNS and firewall: follow the plan's **Setup** section (external TCP 443 → this PC's 8443, UDP 50000
   forwarded to this PC, an A record for your host name, e.g. `glasses.example.com`).
2. Copy `server/appsettings.Local.example.json` to `server/appsettings.Local.json`, put your
   host name in `Web:PublicHost` and your static public IP in `Media:PublicIp`.
3. `.\scripts\run.ps1` (server with tray) and, in another terminal,
   `$env:GLASSES_HOST = 'glasses.example.com'; caddy run --config deploy/Caddyfile`.
4. Open `https://glasses.example.com` (your host) on the laptop tethered to the phone
   (later: on the glasses) and approve the popup.

End a session at any time: the tray menu, or **Ctrl+Alt+Shift+X**.

## Tests

```powershell
dotnet test                                   # ~177 server tests
cd client-web; npm test                        # client unit tests
dotnet run --project tools/e2e-harness         # then, in tools/e2e-harness/browser:
npm run drive                                  # full flow in headless Chrome
```

## Security model, in short

- Only 443 (forwarded to Caddy on 8443) and UDP 50000 are public; the app listens on loopback.
  IIS keeps port 443 on this PC for local use. RDP is never exposed.
- A session exists only after a human approves the matching code on the PC. The token is
  256-bit, single-use, hashed server-side, never in URLs, storage or logs, and it dies with
  the session. The approval also remembers that device for 24 h (fixed, never extended): it
  gets a device token that is swapped on every use; reusing an old one forgets the device.
  **Forget remembered glasses** in the tray ends that early.
- Exact Origin check on both sockets; strict CSP; per-IP and global pairing limits;
  per-session message rate limit; strict allowlisted protocol; text is never followed by
  an automatic Enter.
- Anything suspicious (rejected or timed-out pairing, bad token, wrong Origin, invalid
  messages) raises a throttled Windows notification and lands in the tray's Recent alerts.

## Status

| # | Milestone | State |
| --- | --- | --- |
| M0 | WebRTC + network spike | Done; the spike was removed once the real stream ran on the glasses |
| M1 | Pairing + auth | Done: popup, alerts, hotkey; brief tests 1–9, 15 automated |
| M2 | Desktop streaming | Done: GDI capture, overview/region, H.264 (VP8 fallback) |
| M3 | Input | Done: pointer, click, scroll, Unicode text, allowlisted keys |
| M4 | Hardening | Done in code: CSP, Origin, limits, log hygiene (brief 10–14). External port scan pending |
| M5 | Glasses validation | Largely done (2026-09-25): video, pairing, Pointer, Type and the composer work on the device |
