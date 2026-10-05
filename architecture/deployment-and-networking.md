# Deployment, networking and configuration

This machine's concrete values (hostname, IPs, router rules, IIS) are in CLAUDE.md "This
machine's deployment". This doc explains how the pieces fit and where each setting lives.

## Paths into the PC

```
TCP 443 (internet) → router → 192.168.1.114:8443 Caddy (TLS, Let's Encrypt via TLS-ALPN)
                              → reverse_proxy 127.0.0.1:5080 (Kestrel, loopback only)
UDP 50000 (internet) → router → 192.168.1.114:50000 SIPSorcery (bound to Media:BindAddress)
```

- **Caddy** (`deploy/Caddyfile`): the site address comes from `GLASSES_HOST` (the same name as
  `Web:PublicHost`); `https_port 8443` because IIS owns 443/80 locally; HTTP
  challenge and redirects off; path allowlist (`/`, `/index.html`, `/favicon.svg`, the icons and `/manifest.webmanifest`,
  `/assets/*`, `/health`, `/ws/pair`, `/ws/session`), everything else 404; HSTS, nosniff,
  no-referrer; JSON access log `caddy-access.log` (rolled 10 MiB × 5). Tokens never go in
  URLs, so the log can't hold them.
- **Kestrel** (`ServerApp.Create`): `Urls` from `appsettings.json` (`127.0.0.1:5080`),
  forwarded headers trusted from loopback only (Caddy) with `ForwardLimit = 1`, 16 KB body
  limit, no Server header, WebSocket keep-alive 15 s, security headers + strict CSP on every
  response (`SecurityHeaders`), static client with cache rules (`ClientCaching`).
- **Firewall** (`deploy/firewall.ps1`, admin): TCP 8443, UDP 50000, and TCP 5080 from the
  local subnet only with `-LanTesting`.

## Configuration

Sources, later wins: `appsettings.json` → `appsettings.{Environment}.json` →
`appsettings.Local.json` (git-ignored, machine values, read at startup only) → environment
variables (`Section__Key`) → command line.

| Section | Class | Main keys |
| --- | --- | --- |
| `Web` | `WebOptions` | `PublicHost` (adds `https://host` to the origins and the host to `AllowedHosts`; set in `appsettings.Local.json`), `AllowedOrigins`, `AllowSameOrigin` (forced off outside Development), `ClientRoot` |
| `Pairing` | `PairingOptions` | `RequestTimeout`, `TokenUseWindow`, `MaxRequestsPerIp`, `MaxRequestsGlobal`, `RateWindow`, `DeviceGrantLifetime`, `DeviceGrantFile`, `TakeoverTimeout` |
| `Session` | `ControlSessionOptions` | `AuthTimeout`, `IdleTimeout`, `HeartbeatTimeout`, `MaxDuration`, `MaxMessagesPerSecond` |
| `Media` | `MediaOptions` | see [media-pipeline.md](media-pipeline.md) |
| `Apps` | `AppShortcutOptions` | `Shortcuts[]` {Name, Process, Title} |
| `Desktop` | (raw) | `RegionFile` |
| `Diagnostics` | (raw) | `StatsDirectory` |
| (root) | ASP.NET | `Urls`, `AllowedHosts` |

`Urls` in `appsettings.json` overrides launchSettings `applicationUrl`; the `lan` profile
sets `Urls` as an environment variable for that reason.

## Launch profiles and scripts

| Profile / command | Environment | Listens | Use |
| --- | --- | --- | --- |
| `server` / `run.ps1` | Production | 127.0.0.1:5080 | real use behind Caddy |
| `dev` / `run.ps1 -Dev` | Development | 127.0.0.1:5080 | local browser; also allows Vite on :5173; LAN candidates on |
| `lan` / `run.ps1 -Lan` | Development | 0.0.0.0:5080, `AllowedHosts=*`, same-origin sockets | another device on the home LAN |
| e2e harness | Development | 127.0.0.1:5081 | auto-approve, recorded input (see [testing.md](testing.md)) |

`scripts/run.ps1` builds the client if `dist` is missing and warns when
`appsettings.Local.json` is absent. Keep `.ps1` files ASCII.

## Files on disk at runtime

`%LOCALAPPDATA%\GlassesRemote\`: `device-grant.json` (hashes), `region.json`,
`stats\stats-yyyy-MM-dd.jsonl`. Caddy's certificates: `%APPDATA%\Caddy`.

## Rules

- Public surface: TCP 443 → Caddy and UDP 50000, nothing else. Never RDP, admin endpoints,
  shells or file APIs.
- The server binds loopback except under the `lan` profile.
- `Media:BindAddress` is required on this PC (Ethernet + Wi-Fi).
- Ask before touching firewall, IIS, services or installs.
