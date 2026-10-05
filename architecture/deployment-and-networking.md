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
  (`{ "Web": { "PublicHost": "relay.example.com" } }`), run `start-relay.ps1`, and
  `start-caddy.ps1` (the publish copies `caddy.exe` from `tools\bin` and `Caddyfile.relay` in; it
  takes the host from `relay.Local.json`, `-HttpsPort` to move off 443). Public TCP 443 must
  reach Caddy (TLS-ALPN). On a box whose IIS already serves sites on 443, host the relay in
  IIS instead (below): no Caddy.
- **Run on Linux:** `start-relay.sh`, or the systemd unit `deploy/relay/glasscast-relay.service`
  (state in `/var/lib/glasscast-relay`, sandboxed), and Caddy with the same Caddyfile.
- **Companion:** its server address is `wss://<relay host>/ws/companion` (or build it in:
  `glassesServer` in `android-companion/local.properties`).
- **Unverified (2026-10-05):** the relay has only run on this PC (tests, a published build
  driven by a script); not yet on the user's box, behind Caddy or IIS with a real certificate,
  or with the glasses and the phone.

### On a Windows server with IIS (step by step)

For a box where IIS already serves websites on 443. IIS runs the relay itself (the publish
includes a `web.config` for the ASP.NET Core Module, in-process), starts it on demand after a
reboot and keeps it going; it sits beside the other sites on its own host name (SNI). Caddy,
`caddy.exe`, `Caddyfile.relay` and the start scripts aren't used. Names below are examples:
`relay.example.com`, pool and site `GlasscastRelay`, folders under `C:\GlasscastRelay`.

**Once, on the server (admin):**

1. **Hosting Bundle:** install the *ASP.NET Core Runtime 10 Hosting Bundle* from
   https://dotnet.microsoft.com/download/dotnet/10.0 (Windows, "Hosting Bundle"), then
   `iisreset` (or `net stop was /y; net start w3svc`).
2. **WebSockets in IIS:** `Install-WindowsFeature Web-WebSockets` (Server Manager: Web Server
   (IIS) > Web Server > Application Development > WebSocket Protocol). Without it the glasses
   and phones can't connect.
3. **DNS:** an A record for `relay.example.com` → the server's public IP. TCP 443 (and 80 if
   your certificates use the HTTP check) already reach IIS for the other sites.

**Copy and configure:**

4. Copy the contents of `publish\relay-win-x64` (from `.\scripts\publish-relay.ps1`) to
   `C:\GlasscastRelay\site`, and make `C:\GlasscastRelay\data` (outside the site, so updates
   never touch the phones).
5. In `C:\GlasscastRelay\site` create `relay.Local.json`:
   ```json
   {
     "Web": { "PublicHost": "relay.example.com" },
     "Relay": { "DataDirectory": "C:\\GlasscastRelay\\data" }
   }
   ```

**IIS Manager:**

6. **Application Pools > Add Application Pool:** name `GlasscastRelay`, .NET CLR version **No
   Managed Code**, Integrated. Then its Advanced Settings: *Idle Time-out* `0` and *Regular Time
   Interval* (recycling) `0`, so phones aren't dropped every 20 minutes or 29 hours (they
   reconnect by themselves, but sessions starting at that moment would fail). Keep *Enable
   32-Bit Applications* False and the identity *ApplicationPoolIdentity* (a low-privilege
   virtual account).
7. Give that account access (admin PowerShell):
   ```powershell
   icacls C:\GlasscastRelay\site /grant "IIS AppPool\GlasscastRelay:(OI)(CI)RX"
   icacls C:\GlasscastRelay\data /grant "IIS AppPool\GlasscastRelay:(OI)(CI)M"
   ```
8. **Sites > Add Website:** name `GlasscastRelay`, pool `GlasscastRelay`, physical path
   `C:\GlasscastRelay\site`, binding **https**, IP *All Unassigned*, port **443**, host name
   `relay.example.com`, **Require Server Name Indication** ticked, and its certificate.
   Certificate: however you get them for the other sites. With win-acme: add a temporary **http**
   binding (port 80, same host name) first, run `wacs.exe`, create a certificate for this site
   and let it add the https binding. The relay answers every path itself, so if the HTTP check
   fails, pick win-acme's self-hosting validation (it serves the check through http.sys beside
   IIS). Remove the http binding afterwards if you like; the relay needs only https.

**Check:**

9. `https://relay.example.com/health` says `ok`, `https://relay.example.com/features` says
   `{"pc":false,"phone":true}`, and `https://relay.example.com/` shows Glasscast with only
   **Phone**. If not: Event Viewer > Windows Logs > Application has the relay's warnings and
   errors (and the ASP.NET Core Module's startup errors). For full logs set
   `stdoutLogEnabled="true"` in `web.config`, make `C:\GlasscastRelay\site\logs` with Modify for
   the pool, recycle, and read `logs\stdout_*.log`. At startup the relay logs where it keeps the
   phones, and an error naming the folder if it can't write there.
10. Phone: companion server `wss://relay.example.com/ws/companion`, **Pair** (registers at
    once), **Start**. Glasses: add the web app `https://relay.example.com/`, choose **Phone**,
    type the connect code into the companion's **Connect glasses**, Approve the pairing on the
    phone.

**Updating:**

- *The glasses page only* (most changes): re-run the publish and copy `wwwroot` over
  `C:\GlasscastRelay\site\wwwroot`. No restart; the glasses pick it up on their next Restart.
- *The program* (relay protocol changes): IIS holds the program's files while it runs. Drop a
  file named `app_offline.htm` into `C:\GlasscastRelay\site` (the module stops the relay and
  serves that page), copy the new publish over (it has no `relay.Local.json`, so yours stays),
  then delete `app_offline.htm`. Registered phones are kept (they're in `data`).

### Deploying updates with Web Deploy

`.\scripts\deploy-relay.ps1 -Server relay.example.com -User <windows user>` publishes and deploys
in one go: Web Deploy syncs `publish\relay-win-x64` to the IIS site over
`https://<server>:8172`: it stops the site's app pool for the copy and always starts it again
(Web Deploy's `recycleApp`), moves only changed files and deletes nothing (`relay.Local.json`,
logs and `data` stay), then checks `/health` and `/features`. (Only taking the site offline with
`app_offline.htm` wasn't enough: the relay held its DLLs a while longer, with the phones'
companions connected, and the copy failed with `ERROR_FILE_IN_USE`.) `-NoBuild` deploys the last publish, `-WhatIf` only lists changes. The
password comes from `-Password`, a saved login, `GLASSCAST_DEPLOY_PASSWORD` or a prompt.
`-Save -Server <host> -User <user>` once stores the server and login in
`%LOCALAPPDATA%\GlassesRemote\deploy-relay.clixml`, the password encrypted with DPAPI for that
Windows account; after that `.\scripts\deploy-relay.ps1` alone deploys. The password is never
printed.

Once, on the server (admin PowerShell):

1. The Web Management Service, accepting remote connections:
   ```powershell
   Install-WindowsFeature Web-Mgmt-Service
   Set-ItemProperty HKLM:\SOFTWARE\Microsoft\WebManagement\Server -Name EnableRemoteManagement -Value 1
   Set-Service WMSVC -StartupType Automatic; Restart-Service WMSVC
   ```
2. **Web Deploy** (https://www.iis.net/downloads/microsoft/web-deploy), setup type **Complete**
   (it includes the IIS Deployment Handler that answers on 8172).
3. **Only your IP on 8172:** in the Azure portal, the VM → Networking → add an inbound port rule:
   source *IP addresses* = your home IP, destination port `8172`, TCP, Allow. (Windows Firewall
   gets its rule from step 1; check `Get-NetFirewallRule -DisplayName '*Management*'`.) Nobody
   else can reach the deploy endpoint at all.
4. The account: any local administrator of the server works (Basic authentication over TLS). A
   dedicated one is tidier: a local user that's an administrator, used only for deploying.

The service's certificate is self-signed, hence `-allowUntrusted` in the script; the connection
is still encrypted, and the NSG rule is what keeps others out. This PC has Web Deploy already
(`msdeploy.exe`); another one needs it installed too.

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
