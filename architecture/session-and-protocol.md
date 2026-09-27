# Session lifecycle and control protocol

One authenticated session = one `/ws/session` WebSocket + one WebRTC peer + one frame pump.
Everything the glasses do goes through this socket as small JSON messages.

## Files

| File | Role |
| --- | --- |
| `server/Hosting/GlassesEndpoints.cs` | `SessionAsync`: origin check, 3 s auth deadline, lease, `authenticated`, then `ControlSession.RunAsync`. |
| `server/Sessions/ControlSession.cs` | Runs a session: `hello`, `rtcOffer`, receive loop, frame pump, idle/heartbeat watch, close. `SessionServices` is its DI bundle. |
| `server/Sessions/SocketIO.cs` | Size-capped text reads (16 KB), serialised JSON writes (one send lock), quiet close. |
| `server/Sessions/TokenBucket.cs` | Per-session message rate limit (120/s, burst 240). |
| `server/Sessions/InputController.cs` | Mode + region state; turns input messages into injector calls (see [input-and-desktop.md](input-and-desktop.md)). |
| `server/Protocol/ControlMessages.cs` | Typed messages, `ViewMode`, `KeyCommand` allowlist. |
| `server/Protocol/ControlProtocol.cs` | The strict parser: allowlisted types and properties, caps, clamping, text flattening, stats field list. |
| `client-web/src/protocol.ts` | The client's mirror: `ClientMessage`, `ServerMessage`, `parseServerMessage` (validates everything from the PC). |
| `client-web/src/connection.ts` | `Session` class: opens the socket, sends the auth message, maps close reasons to text. |
| `client-web/src/SessionScreen.tsx` | The connection effect: message handling, ping, stats, visibility, scroll flush. |

## Sequence

```
glasses                                  server
  │── WS upgrade /ws/session ───────────►│ Origin allowlist (403 before accept)
  │── authenticate{token} | resume{token}►│ ≤ 3 s (Session:AuthTimeout) or AuthenticationTimedOut alert
  │◄────────── authenticated{deviceToken?, deviceTokenExpiresAt?}
  │◄────────── hello{monitor, region, mode:"pointer", apps[], codec}
  │◄────────── rtcOffer{sdp}
  │── rtcAnswer{sdp}, iceCandidate{…} ──►│ peer.ApplyAnswer / AddRemoteCandidate
  │── ping{t} every 2 s ────────────────►│◄─ pong{t, serverTime}
  │── move/click/scroll/typeText/key/setMode/setRegion/switchApp ─► InputController / IWindowSwitcher
  │◄────────── region{…} (answer to every setRegion)   appSwitch{slot,result}
  │◄────────── mediaStats{…} ~1/s                      │── stats{…} ~1/s (numbers only)
```

`ControlSession.RunAsync` starts three tasks and waits for the first to finish:
`ReceiveLoopAsync` (returns `null` when the client leaves, or a violation reason),
`FramePump.RunAsync` (see [media-pipeline.md](media-pipeline.md)) and `IdleWatchAsync`.
Everything is linked to one `CancellationTokenSource` that also fires on `lease.Ended`
(terminate/takeover), peer `Closed`, and `MaxDuration` (4 h).

## Messages

Client → server (`ControlProtocol.TryParse`, anything else is a violation):

| type | fields | notes |
| --- | --- | --- |
| `authenticate` / `resume` | `token` ≤128 | first message only; a repeated `authenticate` is a violation |
| `rtcAnswer` | `sdp` ≤12 KB | a rejected answer closes with `bad answer` |
| `iceCandidate` | `candidate` ≤1 KB, `sdpMid`?, `sdpMLineIndex` 0..16 | mDNS candidates are ignored |
| `setMode` | `mode`: overview/view/pointer/scroll/type | View and Scroll have no buttons any more |
| `setRegion` | `x,y,width,height` ints | coarse bounds ±32768, then `RegionMath.Clamp`; always answered with `region` |
| `move` | `x,y` 0..1 in the current view | Pointer mode only |
| `click` | `button:"left"` | Pointer mode only |
| `scroll` | `dy` ≤ ±1200, browser sign | Pointer/Scroll modes |
| `typeText` | `text` ≤500 (the client splits longer text, `textChunks`) | Type mode; newlines/tabs → spaces, control chars dropped, trimmed, never Enter |
| `key` | allowlisted name | Type mode; `Enter`, `Escape`, `Tab`, `Backspace`, `Ctrl+C/V`, `Alt+Tab`, `Win+Shift+Left/Right` |
| `switchApp` | `slot` 1..9 | slots beyond the configured list → `failed` |
| `ping` | `t` | answered with `pong`; any message counts as a heartbeat |
| `stats` | only `ClientStatsFields`, numbers or null | logged, not input (doesn't reset idle) |

Server → client (validated by `parseServerMessage`, unknown or malformed → dropped):
`pairCode`, `paired`, `pairFailed`, `authFailed`, `authenticated`, `hello`, `rtcOffer`,
`region`, `pong`, `appSwitch`, `mediaStats`.

**Keep in sync:** `ControlProtocol.cs` ⇄ `protocol.ts` (types, key names, limits such as
`MAX_TEXT_LENGTH`, `MAX_APPS`, app name length 16) and the anonymous objects sent from
`GlassesEndpoints` / `ControlSession`.

## Limits and timeouts (`ControlSessionOptions`, `Session:*`)

| Setting | Default | Effect |
| --- | --- | --- |
| `AuthTimeout` | 3 s | no auth message → alert + close |
| `HeartbeatTimeout` | 15 s | no message at all → close `no heartbeat` |
| `IdleTimeout` | 60 min | no *input* (pings and stats don't count) → close `idle` |
| `MaxDuration` | 4 h | hard cap |
| `MaxMessagesPerSecond` | 120 (burst ×2) | over → `MessageRateLimited` alert, close `rate limit` |
| message size | 16 KB | over → close `too big`, violation |

## Close reasons (server `closeReason` → client text in `describeClose`)

| Reason | Cause | Device remembered? |
| --- | --- | --- |
| `session ended` | client left, media failed, pump ended | yes |
| `terminated` | tray End session / hotkey | yes |
| `replaced` | same device resumed elsewhere | yes |
| `idle`, `no heartbeat` | watchdog | yes |
| `invalid message`, `rate limit`, `bad answer` | protocol violation (PolicyViolation status) | **no**: server and client both forget |

The client also ends a session itself after `HIDDEN_MS` (5 s) hidden, and on WebRTC `failed`.

## Rules

- Strict parser: unknown `type`, unknown property, wrong kind, oversized → reject → the
  session closes and the device is forgotten. Never turn text into a command.
- Mode gating is on the server (`InputController`), not only in the UI.
- The frame pump must never wait on the control socket: `SendStats` is fire-and-forget.
- Only numbers and fixed names go into `stats` and the stats log.

## Tests

`tests/Protocol/ControlProtocolTests.cs` (every message shape, caps, flattening),
`tests/Sessions/InputControllerTests.cs`, `tests/Hosting/EndpointTests.cs` (full flow, auth
deadline, violations, flooding, heartbeat, terminate), `client-web/src/protocol.test.ts`.
