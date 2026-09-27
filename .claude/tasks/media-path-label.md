# Task: find out why the glasses say "live (remote)" at home

Status: **not started**. Written 2026-09-27. Read `CLAUDE.md` first, then
`architecture/media-pipeline.md` (Connectivity) and `architecture/stats-and-diagnostics.md`.

## What was seen

At the home office, after a server restart, the glasses' status bar said **live (remote)**
although they were at home and should have gone local. The stats log said otherwise:

- Every session since 13:58 on 2026-09-27 has `event: start` with `lanOffered: true` and
  `event: mediaPath` with `lan: true`. The server log says `Media path: 192.168.1.233:<port> (LAN)`.
- 192.168.1.233 is a device on the home Wi-Fi (8 ms ping, randomised MAC, so most likely the
  phone relaying for the glasses).
- The glasses' web traffic (control socket, Caddy log) comes from **187.15.162.139**, not the
  home IP 203.0.113.10: the phone sends it over mobile data or a VPN while the video uses the
  Wi-Fi. That's why `Media:OfferLan` defaults to `Always` (commit 682540c).
- The video round trip (`rttMs` 150–270) looks about the same labelled local or remote. Most of
  it is probably the glasses-to-phone hop.

## Why the two can disagree (hypothesis, not confirmed)

- **Server** (`ControlSession`, `mediaPath` event): looks at the *glasses'* end of the pair
  (`SipsorceryMediaPeer.RemoteMediaEndPoint`). Private address = LAN.
- **Glasses** (`rtc.ts` → `mediaStats.ts` `mediaPath()`, status bar): look at the *PC's* end of
  the pair the browser selected (`remote-candidate` in getStats). Private = local.
- The offer carries two PC addresses, the LAN one (192.168.1.114, ranked first) and the public
  one (203.0.113.10). If the glasses pair with the public one, the router hairpins the packets
  back inside, so the server still sees 192.168.1.233 and calls it LAN. The traffic never leaves
  the house, but the glasses say "remote".
- Which pair wins is probably a race: SIPSorcery nominates the first pair that succeeds
  (`SdpCandidates` comment). The phone relay may also blur which address a check was sent to.
  That would explain "local" on one connection and "remote" on the next.

## What to do

### 1. Measure it: the glasses report their side, only when it changes

Goal: the stats log shows the glasses' view next to the server's `mediaPath` event, without
adding a field to every per-second `stats` line.

- **Client:** in the 1 s stats loop (`SessionScreen.tsx`), take from `VideoReceiver.stats()`
  the selected pair's PC end: `path` (local/remote, already there) and its `candidateType`
  (host = from our offer, prflx = learned from a check, srflx, relay). Send
  `{type:"mediaPath", pc:"local"|"remote", candidate:"host"|"prflx"|"srflx"|"relay"|"unknown"}`
  only when that pair first becomes known and whenever it changes.
- **Server:** accept it in `ControlProtocol` (fixed names only: never an address, see the
  security invariants), and write an `event` line `glassesPath` with `pc` and `candidate`. It's
  a measurement, not input, so it doesn't reset the idle timeout.
- **Compatibility:** an older server rejects an unknown message type as a protocol violation,
  which ends the session *and forgets the glasses*. The server should announce support in
  `hello` (e.g. `reports: ["mediaPath"]`), and the client sends only when it sees it.
- **Keep in sync:** `ControlProtocol.cs` ⇄ `protocol.ts`, the stats-log event list in
  CLAUDE.md ("Measuring on the device"), `architecture/session-and-protocol.md` (message table)
  and `architecture/stats-and-diagnostics.md`.
- **Tests:** parser tests (accepts the fixed names, rejects addresses and unknown names), an
  endpoint test (the event lands in the stats log, `hello` lists the report), a client test for
  sending only on change, and an e2e check (the harness should log `glassesPath` with `pc: local`).

### 2. Then decide, from a few sessions at home

Look for `start` → `mediaPath` → `glassesPath` per session in
`%LOCALAPPDATA%\GlassesRemote\stats\stats-yyyy-MM-dd.jsonl`:

- `lan: true` with `pc: remote`: the hairpin case. Options:
  - make the status bar follow the server's view (the server sends its `mediaPath` result to
    the glasses; "local" = the glasses' address is on the LAN);
  - stop offering the public address when the glasses are provably at home (can't be decided
    from the control socket's IP here, because it comes over mobile data);
  - have the PC prefer nominating the LAN pair when both succeed (look at how SIPSorcery
    nominates; it may not be configurable).
- `lan: true` with `pc: local`: consistent; the earlier "remote" was another cause, dig further.
- `candidate: prflx`: the browser learned the PC's address from a check rather than from the
  offer, which points at the phone relay rewriting addresses.

Also compare `rttMs` and `arrivalMs` between `pc: local` and `pc: remote` sessions, to see
whether the hairpin costs anything noticeable.

## Not verified

Nothing here was reproduced on purpose; it comes from one session's logs. It needs the glasses
at home, several connects, and the stats log read afterwards.
