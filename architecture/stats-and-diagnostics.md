# Stats and diagnostics

How latency and link health are measured, shown on the glasses, and logged on the PC so a
session can be read back afterwards. The field lists and the "what each number means" live in
CLAUDE.md ("Measuring on the device"); this doc explains the machinery.

## Files

| File | Role |
| --- | --- |
| `server/Media/FramePump.cs` | `StatsWindow` / `MediaStats`: per-second capture, encode, send delay, kbps, keyframes, requests, and `[rtp, capturedAtUnixMs, bytes]` per frame. |
| `server/Media/RtpPacer.cs` | `TakeSendDelay`: handed-over → last packet sent, per frame. |
| `server/Media/SipsorceryMedia.cs` | `TakeSendStats`: pacer delay + NACKed/resent counts. |
| `server/Sessions/ControlSession.cs` | `SendStats`: builds `mediaStats` (fire-and-forget) and the `pc` log line; writes `event` lines (start, setMode, switchApp, end); logs `glasses` lines from the `stats` message. |
| `server/Media/StatsLog.cs` | Appends JSON lines to `%LOCALAPPDATA%\GlassesRemote\stats\stats-yyyy-MM-dd.jsonl` (`Diagnostics:StatsDirectory` overrides). |
| `server/Protocol/ControlProtocol.cs` | `ClientStatsFields`: the only names a `stats` message may carry. |
| `client-web/src/mediaStats.ts` | `ClockSync`, `FrameLatency`, `receiverStats`, `statsLines` (panel text), `statsReport` (what goes to the PC), network codes. |
| `client-web/src/rtc.ts` | `VideoReceiver.stats()` (getStats snapshot, selected pair RTT, ICE network type, `navigator.connection`), `watchFrames` (requestVideoFrameCallback). |
| `client-web/src/SessionScreen.tsx` | Wires it up: pong → clock, mediaStats → sent frames, rVFC → shown frames, 1 s interval → panel + `stats` message. |
| `server/Media/LinkTest.cs` | Diagnostic bitrate staircase. |

## Capture-to-display latency

```
PC: frame captured at T_pc (Unix ms, PC clock), sent with RTP timestamp R
    mediaStats.frames = [[R, T_pc, bytes], …]  (once a second)
glasses: rVFC reports R with receiveTime and expectedDisplayTime (local clock)
    FrameLatency matches R on both sides (5 s window)
    ClockSync: offset from the ping/pong with the smallest RTT (error ±RTT/2)
    e2e = shownAt − (T_pc − offset);  PC→here = receivedAt − (T_pc − offset);  buffer+show = shownAt − receivedAt
```

Averages over 2 s, maxima over 10 s. Not included: the wait for the next capture tick
(0–50 ms at 20 fps) and display scan-out. `framesShown` 0 in the log means the browser gave
no per-frame RTP timestamps.

## The stats log

One JSON object per line: `t`, `session`, `kind` and the fields. `kind` is `glasses` (from
the client's `stats`), `pc` (from `SendStats`) or `event`. Stats messages don't count as input
for the idle timeout but do count as a heartbeat. The e2e harness writes to
`%TEMP%\glasses-e2e-stats`. The log is never pruned.

## Keeping the three field lists in sync

`ControlProtocol.ClientStatsFields` ⇄ `StatsReport` (`protocol.ts`) ⇄ `statsReport()`
(`mediaStats.ts`); and the `pc` line in `ControlSession.SendStats` ⇄ `PcMediaStats` parsing
in `parseServerMessage`. A new client field that the server doesn't list makes the server
reject the message and close the session, so ship the server side first. The client only
starts sending `stats` after the first `mediaStats`, as a guard against older servers.

## Link test

`Media:LinkTestOnStart=true` (env `Media__LinkTestOnStart`) replaces the first
`steps × LinkTestStepSeconds` of every session with noise covering `kbps / maxStep` of the
frame, at 500/1000/2000/4000/8000 kbit/s. The bitrate controller records but doesn't adapt
during the test. Compare the `pc` kbps with the glasses' kbps/lost/arrivalMs per step; run it
on the glasses and in the phone's browser to split the phone-to-glasses hop from the internet.

## Tests

`tests/Media/FramePumpTests.cs` (per-frame timings, link test), `tests/Hosting/EndpointTests.cs`
(`Stats_from_the_glasses_and_the_pc_go_to_the_stats_log…`, session events),
`client-web/src/mediaStats.test.ts`. e2e: "the Stats panel shows capture-to-display latency…",
"the glasses' and the PC's figures reach the PC's stats log".
