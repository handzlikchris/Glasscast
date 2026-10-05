# Phone mode for other people: a hosted relay and publishing the repo

Status: **idea, parked 2026-10-05.** The user isn't sharing it for now ("for now this is just for
me"); anyone who wants it can deploy the server and run the relay themselves. Pick this up only
when asked.

## Already done (2026-10-05)

- The real host name and public IP were removed from every file and commit message, all history
  rewritten with `git filter-repo` (backup bundle: `E:\_src-unity\GlassesRemote-before-scrub-2026-10-05.bundle`).
  Tracked files use `glasses.example.com` / `203.0.113.x`.
- The host is configuration now: `Web:PublicHost` (server), `GLASSES_HOST` (Caddyfile),
  `glassesServer` in `android-companion/local.properties` (the companion's default address,
  `BuildConfig.DEFAULT_SERVER`). Machine facts are in the git-ignored `CLAUDE.local.md`.
- **Not pushed.** The rewrite removed the `origin` remote. Local `main` and `feat/phone-mode`
  no longer share commits with `origin` (github.com/handzlikchris/GlassesRemote, private).

## Before publishing the repo

- **Where to push:** a new GitHub repo, or delete and recreate the old one. Force-pushing over
  the existing repo leaves old commits reachable by hash on GitHub until support purges them.
  Ask the user; never force-push without a go-ahead.
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
