# Phone mode for other people: a hosted relay and publishing the repo

Status: **in progress on branch `feat/public-phone-relay` (started 2026-10-05).** First step: a
hosted relay the user runs on their own server box (Windows, behind Caddy, started by a script),
that others could run too (Linux, no IIS). Publishing the repo comes later.

## Plan (decided 2026-10-05)

User's requirements: the box runs **only** the relay and the glasses page: no capture, input,
window or media code may even be in that binary ("this is my server, there's plenty of stuff
there"). Limit changes. Many phones from the start. The hosted page is phone only.

- **Projects.** `relay/` (library `GlassesRemote.Relay`, `net10.0`): the phone relay
  (`server/Phone/*`) and the small helpers it needs (alerts, secrets, rate limiters, `SocketIO`,
  JSON rule helpers), moved with their namespaces kept. `relay-server/`
  (`GlassesRemote.RelayServer`, `net10.0` web app): `/health`, `/ws/session` (phone only),
  `/ws/companion`, the glasses page. It references only `relay/`; a test checks it loads no PC
  assembly (`GlassesRemote.Server`, SIPSorcery, NAudio, Vortice). The PC server references
  `relay/` and keeps phone mode.
- **Many phones.** Phones are kept by id (random 128-bit) with their token hash, name, first and
  last seen (`phones.json`; the PC's old `companion-grant.json` imported once). Registration is a
  setting, `Companion:Registration`: `Approve` (the PC: the popup, as today) or `Open` (the relay
  server: `paired{token}` at once, rate-limited per IP and overall, capped by `MaxPhones`, phones
  unseen for `ForgetAfter` dropped). The companion's protocol for it is unchanged.
- **Routing.** The glasses open `{type:"phone", phone?}`. A known id waits for that phone, as
  today. No id or an unknown one: the server sends `connectCode` (short, single use, minutes),
  the user types it into the companion, which sends `claim{code}`; the server joins that relay
  to that phone and tells the glasses the phone's id (`phoneFound`), which they keep next to the
  pairing (localStorage). Then the existing pairing (code on both, Approve on the phone) runs:
  the server stays untrusted. Claims rate-limited per phone.
- **Page.** `GET /features` says which targets the server offers; the relay server says phone
  only and the first screen goes straight to Phone.
- **Deploy.** `scripts/run-relay.ps1`, a Caddyfile for the relay host, a linux-x64 publish
  (systemd unit) for others. Docs: `architecture/phone-mode.md`, `deployment.md`, CLAUDE.md.

Commits in that order: plan, move to `relay/` (no behaviour change), many phones + connect
codes (server, tests), relay server (+ tests), client, companion, deploy + docs.

## Already done (2026-10-05)

- The real host name and public IP were removed from every file and commit message, all history
  rewritten with `git filter-repo` (backup bundle: `E:\_src-unity\GlassesRemote-before-scrub-2026-10-05.bundle`).
  Tracked files use `glasses.example.com` / `203.0.113.x`.
- The host is configuration now: `Web:PublicHost` (server), `GLASSES_HOST` (Caddyfile),
  `glassesServer` in `android-companion/local.properties` (the companion's default address,
  `BuildConfig.DEFAULT_SERVER`). Machine facts are in the git-ignored `CLAUDE.local.md`.
- **Pushed over the old history (2026-10-05)**, to the same repo, renamed **Glasscast**
  (github.com/handzlikchris/Glasscast, private). The user chose this over a fresh repo: the old
  commits stay reachable on GitHub by hash (short hashes can be brute-forced) until GitHub
  support purges them. Ask them to request a purge before making the repo public.

## Before publishing the repo

- **Package name** `uk.co.reliablesolutions.glassesremote.companion` names the user's company.
  Renaming means a new app on the phone (reinstall, accessibility again, pair again). Ask.
- Harmless leftovers the user may want gone: LAN addresses (192.168.1.x), the PC name in old
  commits, the S25 model, the private claude.ai artifact links in CLAUDE.md.
- Re-check: `git grep -i` over `$(git rev-list --all)` and `git log --all --format=%B` for the
  real host and IP (patterns in `CLAUDE.local.md`) must find nothing.

## How phone mode reaches the phone today (why it can't be shared as is)

The server is only the meeting point: the glasses open a relay (`/ws/session {type:"phone"}`),
the companion holds `/ws/companion`, pairing and the WebRTC offer/answer pass through it, then
the glasses close the relay and the session runs phone ↔ glasses through Meta's app (no video
through the server, so hosting costs almost nothing). Three things stop it serving others:

1. **The companion registers through the PC's Approve popup** (`ApprovePopup`,
   `CompanionRegistry`, `companion-grant.json`). A hosted server has no one to click it.
2. **One companion per server.** The glasses ask for "the phone" with no ID; a second
   companion replaces the first (`CompanionRegistry`, one `CompanionLink`, one relay).
3. **The relay lives in the Windows tray app** (`net10.0-windows`, WinForms), which can't run
   on ordinary Linux hosting.

Dropping the PC approval is safe: the phone is the gate (ECDH code on both screens + Approve on
the phone, proof every session, Android's capture consent every session). The registration only
stopped a stranger taking the single companion slot, which per-phone routing removes.

## Sketch

- **Headless relay host:** a cross-platform ASP.NET project (Linux/container) serving the glasses
  web app plus the phone relay (`server/Phone/*` minus the popup), behind TLS. PC mode stays in
  the Windows app for people who run their own; the hosted page hides PC or labels it.
- **Many phones:** the companion self-registers (random id + secret, hash kept on the server,
  rate-limited, no approval). First connection: the glasses show a short connect code, the user
  types it into the companion (typing is easy on the phone); the server joins that relay to that
  phone, then the existing pairing runs. The glasses then remember the phone id and route by it.
  Keep the numeric comparison: the server stays untrusted.
- **Companion build with the server baked in** (`glassesServer`) so nobody types an address;
  an APK download on the hosted page.
- Keep the security invariants: server holds no pairing key, no input on a relay, `/ws/companion`
  refuses requests with an `Origin` header, strict parsers, rate limits per IP and per phone.

## Hurdles for non-technical users

- Sideloaded APK: Android needs App info → ⋮ → **Allow restricted settings** before
  accessibility can be turned on; the setup screen should walk them through it.
- Play Store would remove that, but Google reviews accessibility-service apps strictly
  (disclosure, declared use); approval for remote control isn't certain.
- Square screen needs `WRITE_SECURE_SETTINGS` over adb: stays optional.
- Unverified: how someone else adds a web app by URL on their Meta glasses.
