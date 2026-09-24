# Glasses Remote Desktop

Proof of concept: control a Windows desktop from a Meta Ray-Ban Display web app.
The glasses view a region of the primary monitor over low-latency WebRTC video,
drive the mouse with Neural Band pinches, and type through the glasses' voice or
handwriting composer. A single-use pairing, approved in a popup on the PC, gates
every session.

The full plan (architecture, pairing model, milestones, risks, router/DNS setup)
lives in the shared plan page. This README covers the repo itself.

## Layout

| Folder | What it is |
| --- | --- |
| `spikes/webrtc/` | M0: throwaway WebRTC + networking spike (synthetic test pattern, no auth) |
| `server/` | ASP.NET Core app, runs as the logged-in Windows user: pairing, WebRTC, capture, input, approve popup |
| `client-web/` | Vite + React + TypeScript glasses client (600×600) |
| `tests/` | Server test suite (xUnit) |
| `deploy/` | Caddyfile and Windows firewall script |

## Requirements

- Windows 10/11, .NET 8 SDK
- Node 20+
- Caddy (stock build) for public HTTPS
- Router: TCP 443 and UDP 50000 forwarded to this PC (see the plan's Setup section)

## Milestones

| # | Milestone |
| --- | --- |
| M0 | WebRTC + network spike |
| M1 | Pairing + auth |
| M2 | Desktop streaming |
| M3 | Input |
| M4 | Hardening |
| M5 | Glasses validation (when the device arrives) |
