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
  `/assets/*`, `/health`, `/features`, `/ws/pair`, `/ws/session`, `/ws/companion`), everything else 404; HSTS, nosniff,
  no-referrer; JSON access log `caddy-access.log` (rolled 10 MiB × 5). Tokens never go in
  URLs, so the log can't hold them.
- **Kestrel** (`ServerApp.Create`): `Urls` from `appsettings.json` (`127.0.0.1:5080`),
  forwarded headers trusted from loopback only (Caddy) with `ForwardLimit = 1`, 16 KB body
  limit, no Server header, WebSocket keep-alive 15 s, security headers + strict CSP on every
  response (`SecurityHeaders`), static client with cache rules (`ClientCaching`). All of this is
  `relay/Hosting/WebHosting.cs`, shared with the relay server below.
- **Firewall** (`deploy/firewall.ps1`, admin): TCP 8443, UDP 50000, and TCP 5080 from the
  local subnet only with `-LanTesting`.

## Hosted phone relay (`relay-server/`, 2026-10-05)

A second, headless server for **phone mode only**: it serves the glasses page and relays the
first connection between glasses and phones (pairing, proof, offer, answer), nothing else. It
references only `relay/` (no capture, input, window or media code; a test walks its
references), runs on Windows or Linux (x64 or arm64), and has no tray and no popup. Meant for a
box that must never be controllable from the glasses, and for other people's phones.

```
glasses ──HTTPS+WSS──► :443 Caddy (deploy/Caddyfile.relay) ──► 127.0.0.1:5090 relay server
phones' companions ──WSS /ws/companion──┘                      (/, /assets, /health, /features,
                                                                 /ws/session, /ws/companion)
phone ⇄ glasses WebRTC (video, input): straight between them, never through the relay
```

- **What differs from the PC server:** `/features` says `{pc:false, phone:true}`, so the page
  shows only Phone; `/ws/session` takes only `{type:"phone", phone?}` (anything else:
  `authFailed`); no `/ws/pair`. Phones always register themselves (`Companion:Registration`
  forced to `Open`; rate-limited, `MaxPhones`, `ForgetAfter`), and the glasses find theirs with
  a connect code the first time (see [phone-mode.md](phone-mode.md)). Only TCP 443 is public.
- **Settings:** `relay.json` next to the program, then `relay.Local.json` (git-ignored:
  `Web:PublicHost`), then environment variables (`Web__PublicHost`), then arguments. Their own
  names, so they never mix with the PC server's `appsettings.json`. `Relay:DataDirectory` holds
  `phones.json` (token hashes only); default `LocalAppData/GlassesRemote/relay`, never the PC
  server's files. `Web:ClientRoot` is `wwwroot` (the launch profiles use `../client-web/dist`).
- **Publish:** `.\scripts\publish-relay.ps1 [-Runtime win-x64|linux-x64|linux-arm64] [-Output dir]`
  builds the page into `client-web/dist-relay` (not `dist`, which a running PC server serves),
  publishes self-contained (no .NET needed on the box, ~106 MB) and copies the page to
  `wwwroot` and the start scripts next to the program.
- **Run on Windows:** copy the folder over, add `relay.Local.json`
  (`{ "Web": { "PublicHost": "relay.example.com" } }`), run `start-relay.ps1`, and Caddy:
  `$env:RELAY_HOST='relay.example.com'; caddy run --config Caddyfile.relay`. Public TCP 443 must
  reach Caddy (TLS-ALPN). If IIS holds 443 on the box, forward 443 to another port and set
  `HTTPS_PORT`, as the PC does with 8443, or let IIS proxy instead (ARR + URL Rewrite with
  WebSockets on, to `127.0.0.1:5090`, keeping the same path allowlist).
- **Run on Linux:** `start-relay.sh`, or the systemd unit `deploy/relay/glasscast-relay.service`
  (state in `/var/lib/glasscast-relay`, sandboxed), and Caddy with the same Caddyfile.
- **Companion:** its server address is `wss://<relay host>/ws/companion` (or build it in:
  `glassesServer` in `android-companion/local.properties`).
- **Unverified (2026-10-05):** the relay has only run on this PC (tests, a published build
  driven by a script); not yet on the user's box, behind Caddy with a real certificate, or with
  the glasses and the phone.

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
| e2e harness | Development | 127.0.0.1:5081 | auto-approve, recorded input (see [testing.md](testing.md)); `E2E_CLIENT_ROOT` serves another client build |
| `relay` / `relay-dev` (relay-server) | Production / Development | 127.0.0.1:5090 | the hosted phone relay, page from `client-web/dist` |

`scripts/run.ps1` builds the client if `dist` is missing and warns when
`appsettings.Local.json` is absent. Keep `.ps1` files ASCII.

## Files on disk at runtime

`%LOCALAPPDATA%\GlassesRemote\`: `device-grant.json` (hashes), `phones.json` (the registered
phones: token hashes; the old `companion-grant.json` is imported once), `region.json`,
`stats\stats-yyyy-MM-dd.jsonl`. The relay server: `relay\phones.json` there, or
`Relay:DataDirectory`. Caddy's certificates: `%APPDATA%\Caddy`.

## Rules

- Public surface: TCP 443 → Caddy and UDP 50000, nothing else. Never RDP, admin endpoints,
  shells or file APIs.
- The server binds loopback except under the `lan` profile.
- `Media:BindAddress` is required on this PC (Ethernet + Wi-Fi).
- Ask before touching firewall, IIS, services or installs.
