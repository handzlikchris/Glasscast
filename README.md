# Glasses Remote Desktop

Proof of concept: control a Windows desktop from a Meta Ray-Ban Display web app.
The glasses view a region of the primary monitor over low-latency WebRTC video
(H.264), drive the mouse with Neural Band pinches, and type through the glasses'
voice or handwriting composer. A single-use pairing, approved in a popup on the
PC, gates every session.

The shared plan page has the design, decisions, risks and the router/DNS setup.
This README covers the code.

## Layout

| Folder | What it is |
| --- | --- |
| `server/` | ASP.NET Core + WinForms app, runs as the logged-in user: pairing, WebRTC, capture, input, tray, approve popup |
| `client-web/` | Vite + React + TypeScript glasses client (600×600) |
| `tests/` | Server tests (xUnit): unit, WebSocket integration, real H.264 encoder |
| `tools/e2e-harness/` | Dev-only harness + headless-Chrome script that drives the whole flow |
| `spikes/webrtc/` | M0 spike: unauthenticated test-pattern stream for the network test |
| `deploy/` | Caddyfiles and the Windows firewall script |
| `scripts/` | `run.ps1`: build the client and start the server |

## How it fits together

```
glasses / laptop ──HTTPS+WSS :443──► Caddy ──► 127.0.0.1:5080 .NET server ──► SendInput
        ▲                                         │   GDI capture → H.264 (Media Foundation)
        └───────────── WebRTC video, UDP 50000 ◄──┘   (VP8 fallback)
```

- **Pairing** (`/ws/pair`): the glasses get a 6-character code; a popup on the PC shows
  the same code, and **Approve** sends a single-use 256-bit token to that socket only.
- **Session** (`/ws/session`): the first message must be `authenticate` within 3 s.
  Then: `hello` (monitor size, region), the WebRTC offer, and control messages.
- **Media**: SIPSorcery on the fixed UDP port 50000. The offer advertises the router's
  public IP, so the glasses connect straight through the port forward, with no STUN/TURN.
- **Modes** give pinch-drag one meaning at a time: Overview (move the region box),
  View, Pointer (drag moves the cursor, a short pinch clicks), Scroll, Type.

## Requirements

- Windows 10/11 (capture, SendInput and the tray are Windows-only)
- .NET 10 SDK (LTS, supported to Nov 2028; pinned by `global.json`)
- Node 20+ for the client
- Caddy (stock build) for public HTTPS

## Run locally (no Caddy, no glasses)

```powershell
.\scripts\run.ps1 -Dev
# open http://127.0.0.1:5080 in Chrome (600×600 is the glasses' size)
# approve the popup on the PC; the tray icon turns green
```

## Run for real

1. Router, DNS and firewall: follow the plan's **Setup** section (TCP 443 and UDP 50000
   forwarded to this PC, an A record for `glasses.example.com`).
2. Copy `server/appsettings.Local.example.json` to `server/appsettings.Local.json` and put
   your static public IP in `Media:PublicIp`.
3. `.\scripts\run.ps1` (server with tray) and, in another terminal,
   `caddy run --config deploy/Caddyfile`.
4. Open `https://glasses.example.com` on the laptop tethered to the phone
   (later: on the glasses) and approve the popup.

End a session at any time: the tray menu, or **Ctrl+Alt+Shift+X**.

## Tests

```powershell
dotnet test                                   # 100 server tests
cd client-web; npm test                        # client unit tests
dotnet run --project tools/e2e-harness         # then, in tools/e2e-harness/browser:
npm run drive                                  # full flow in headless Chrome
```

## Security model, in short

- Only 443 (Caddy) and UDP 50000 are public; the app listens on loopback. RDP is never exposed.
- A session exists only after a human approves the matching code on the PC. The token is
  256-bit, single-use, hashed server-side, never in URLs, storage or logs, and it dies with
  the session. There's no reconnection without a new approval.
- Exact Origin check on both sockets; strict CSP; per-IP and global pairing limits;
  per-session message rate limit; strict allowlisted protocol; text is never followed by
  an automatic Enter.
- Anything suspicious (rejected or timed-out pairing, bad token, wrong Origin, invalid
  messages) raises a throttled Windows notification and lands in the tray's Recent alerts.

## Status

| # | Milestone | State |
| --- | --- | --- |
| M0 | WebRTC + network spike | Built; local runs pass (19–24 ms p50). Hotspot run pending router setup |
| M1 | Pairing + auth | Done: popup, alerts, hotkey; brief tests 1–9, 15 automated |
| M2 | Desktop streaming | Done: GDI capture, overview/region, H.264 (VP8 fallback) |
| M3 | Input | Done: pointer, click, scroll, Unicode text, allowlisted keys |
| M4 | Hardening | Done in code: CSP, Origin, limits, log hygiene (brief 10–14). External port scan pending |
| M5 | Glasses validation | Waiting for the device |
